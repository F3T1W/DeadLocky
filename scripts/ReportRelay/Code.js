/* Google Apps Script V8. No request contents are logged or persisted. */
const RECIPIENT = 'f3t1w@icloud.com';
const MAX_ZIP = 8 * 1024 * 1024;
const MAX_REQUEST = 12 * 1024 * 1024;
const RETENTION_MS = 7 * 24 * 60 * 60 * 1000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

function doGet() {
    const enabled = PropertiesService.getScriptProperties().getProperty('REPORTS_ENABLED') === 'true';
    return json_({ service: 'DeadLockyReports', enabled: enabled });
}

function doPost(event) {
    let report;
    let bytes;
    try {
        if (!event || !event.postData || event.postData.type.split(';')[0] !== 'application/json') {
            return json_({ ok: false, error: 'invalid' });
        }
        const raw = event.postData.contents;
        if (typeof raw !== 'string' || raw.length > MAX_REQUEST || utf8Size_(raw) > MAX_REQUEST) {
            return json_({ ok: false, error: 'size' });
        }
        const payload = JSON.parse(raw);
        requireKeys_(payload, ['report', 'archive']);
        report = validateReport_(payload.report);
        if (typeof payload.archive !== 'string' || payload.archive.length > Math.ceil(MAX_ZIP / 3) * 4
            || payload.archive.length % 4 !== 0 || /[^A-Za-z0-9+/=]/.test(payload.archive)
            || (payload.archive.indexOf('=') >= 0 && payload.archive.indexOf('=') < payload.archive.length - 2)) {
            throw new Error('Invalid base64');
        }
        bytes = Utilities.base64Decode(payload.archive);
        if (bytes.length < 22 || bytes.length > MAX_ZIP || (bytes[0] & 255) !== 80
            || (bytes[1] & 255) !== 75 || (bytes[2] & 255) !== 3 || (bytes[3] & 255) !== 4
            || Utilities.base64Encode(bytes) !== payload.archive) {
            throw new Error('Invalid ZIP');
        }
    } catch (_) {
        return json_({ ok: false, error: 'invalid' });
    }

    const id = report.id;
    const lock = LockService.getScriptLock();
    if (!lock.tryLock(5000)) { return json_({ ok: false, id: id, error: 'busy' }); }
    try {
        const props = PropertiesService.getScriptProperties();
        const now = Date.now();
        const key = 'report.' + id;
        const all = props.getProperties();
        let retained = 0;
        Object.keys(all).filter(function (name) { return name.indexOf('report.') === 0; }).forEach(function (name) {
            const state = JSON.parse(all[name]);
            if (now - state.time > RETENTION_MS) { props.deleteProperty(name); }
            else { retained++; }
        });
        const previous = props.getProperty(key);
        if (previous) {
            const state = JSON.parse(previous);
            return json_(state.state === 'sent' ? { ok: true, id: id }
                : { ok: false, id: id, error: 'uncertain' });
        }
        if (props.getProperty('REPORTS_ENABLED') !== 'true') {
            return json_({ ok: false, id: id, error: 'disabled' });
        }
        const configured = Number(props.getProperty('DAILY_LIMIT') || 20);
        const limit = Number.isInteger(configured) ? Math.max(1, Math.min(50, configured)) : 20;
        let budget = JSON.parse(props.getProperty('budget') || 'null');
        if (!budget || now - budget.start >= 24 * 60 * 60 * 1000) {
            budget = { start: now, count: 0, burstStart: now, burstCount: 0 };
        }
        if (now - budget.burstStart >= 10 * 60 * 1000) {
            budget.burstStart = now;
            budget.burstCount = 0;
        }
        if (retained >= 200 || budget.count >= limit || budget.burstCount >= 5
            || MailApp.getRemainingDailyQuota() < 1) {
            return json_({ ok: false, id: id, error: 'quota' });
        }
        // Persist intent BEFORE sending. Any ambiguous failure requires owner investigation, never an automatic resend.
        budget.count++;
        budget.burstCount++;
        props.setProperty('budget', JSON.stringify(budget));
        props.setProperty(key, JSON.stringify({ state: 'pending', time: now }));
        try {
            const options = {
                to: RECIPIENT,
                subject: 'DeadLocky problem report ' + id,
                body: 'Report ID: ' + id + '\n\n' + report.message + '\n\nDiagnostics:\n'
                    + JSON.stringify(report.metadata, null, 2)
                    + '\n\nThe attached ZIP includes the description and optional reply address.',
                attachments: [Utilities.newBlob(bytes, 'application/zip', 'DeadLocky-report-' + id + '.zip')],
                name: 'DeadLocky Reports'
            };
            if (report.email) { options.replyTo = report.email; }
            MailApp.sendEmail(options);
            props.setProperty(key, JSON.stringify({ state: 'sent', time: now }));
            return json_({ ok: true, id: id });
        } catch (_) {
            try { props.setProperty(key, JSON.stringify({ state: 'uncertain', time: now })); } catch (_) { /* Pending is also uncertain. */ }
            return json_({ ok: false, id: id, error: 'uncertain' });
        }
    } catch (_) {
        // A property or quota service failure must not trigger another send.
        return json_({ ok: false, id: id, error: 'uncertain' });
    } finally {
        lock.releaseLock();
    }
}

function validateReport_(report) {
    requireKeys_(report, ['id', 'message', 'email', 'metadata']);
    if (typeof report.id !== 'string' || !UUID.test(report.id)
        || typeof report.message !== 'string' || !report.message.trim()
        || report.message.length > 8000 || utf8Size_(report.message) > 32000
        || typeof report.email !== 'string' || report.email.length > 254
        || (report.email && !/^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+$/.test(report.email))) {
        throw new Error('Invalid fields');
    }
    const metadata = report.metadata;
    requireKeys_(metadata, ['appVersion', 'build', 'os', 'runtime', 'architecture', 'sessionId'], ['gameBuild']);
    ['appVersion', 'build', 'os', 'runtime', 'architecture'].forEach(function (name) {
        if (typeof metadata[name] !== 'string' || metadata[name].length > 256 || /[\r\n\x00]/.test(metadata[name])) {
            throw new Error('Invalid metadata');
        }
    });
    if (typeof metadata.sessionId !== 'string' || !UUID.test(metadata.sessionId)
        || (metadata.gameBuild != null && (typeof metadata.gameBuild !== 'string' || !/^\d{1,20}$/.test(metadata.gameBuild)))) {
        throw new Error('Invalid metadata');
    }
    report.id = report.id.toLowerCase();
    return report;
}

function requireKeys_(object, required, optional) {
    if (!object || typeof object !== 'object' || Array.isArray(object)
        || required.some(function (key) { return !Object.prototype.hasOwnProperty.call(object, key); })
        || Object.keys(object).some(function (key) { return required.indexOf(key) < 0 && (optional || []).indexOf(key) < 0; })) {
        throw new Error('Invalid schema');
    }
}
function utf8Size_(text) { return Utilities.newBlob(text).getBytes().length; }
function json_(value) { return ContentService.createTextOutput(JSON.stringify(value)).setMimeType(ContentService.MimeType.JSON); }

// Run manually from the owner's editor, never exposed as HTTP routes.
function enableReports() { PropertiesService.getScriptProperties().setProperty('REPORTS_ENABLED', 'true'); }
function disableReports() { PropertiesService.getScriptProperties().setProperty('REPORTS_ENABLED', 'false'); }
