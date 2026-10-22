const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const test = require('node:test');
const source = fs.readFileSync(__dirname + '/Code.js', 'utf8');
const id = 'b5182161-9a14-41a8-b879-229f1ab3066c';
function fixture() {
    const properties = { REPORTS_ENABLED: 'true' };
    const sent = [];
    const state = { quota: 100, failure: false, locked: false, released: 0 };
    const props = {
        getProperty: name => properties[name] || null,
        getProperties: () => ({ ...properties }),
        setProperty: (name, value) => { properties[name] = value; },
        deleteProperty: name => { delete properties[name]; }
    };
    const context = vm.createContext({
        PropertiesService: { getScriptProperties: () => props },
        Utilities: {
            newBlob: (data, type, name) => ({ getBytes: () => [...Buffer.from(data)], type, name }),
            base64Decode: text => [...Buffer.from(text, 'base64')],
            base64Encode: bytes => Buffer.from(bytes).toString('base64')
        },
        ContentService: { MimeType: { JSON: 'application/json' }, createTextOutput: text => ({
            text, setMimeType() { return this; }
        }) },
        LockService: { getScriptLock: () => ({ tryLock: () => !state.locked, releaseLock: () => { state.released++; } }) },
        MailApp: { getRemainingDailyQuota: () => state.quota, sendEmail: options => {
            assert.equal(JSON.parse(properties['report.' + id] || '{"state":"pending"}').state, 'pending');
            sent.push(options);
            if (state.failure) { throw new Error('Uncertain service failure'); }
        } }
    });
    vm.runInContext(source, context);
    const zip = Buffer.alloc(24); zip.write('PK\x03\x04', 'binary');
    const payload = () => ({ report: { id, message: 'Problem\nDescription', email: 'reply@example.com', metadata: {
        appVersion: '0.2.0', build: '2', os: 'macOS', runtime: '.NET 10', architecture: 'Arm64', sessionId: id, gameBuild: null
    } }, archive: zip.toString('base64') });
    return { context, properties, sent, state, payload, post(value) {
        return JSON.parse(context.doPost({ postData: { type: 'application/json', contents: JSON.stringify(value) } }).text);
    } };
}
test('fixed recipient, validated replyTo, ZIP attachment and matching receipt', () => {
    const f = fixture();
    assert.deepEqual(f.post(f.payload()), { ok: true, id });
    assert.equal(f.sent[0].to, 'f3t1w@icloud.com');
    assert.equal(f.sent[0].replyTo, 'reply@example.com');
    assert.equal(f.sent[0].attachments[0].type, 'application/zip');
    assert.equal(f.state.released, 1);
    assert.ok(!JSON.stringify(f.properties).includes('Description'));
    assert.ok(!JSON.stringify(f.properties).includes('reply@example.com'));
});
test('duplicate receipt retry sends once, even when daily quota later exhausted', () => {
    const f = fixture();
    f.post(f.payload()); f.state.quota = 0;
    assert.deepEqual(f.post(f.payload()), { ok: true, id });
    assert.equal(f.sent.length, 1);
});
test('uncertain send is never automatically retried', () => {
    const f = fixture(); f.state.failure = true;
    assert.equal(f.post(f.payload()).error, 'uncertain');
    f.state.failure = false;
    assert.equal(f.post(f.payload()).error, 'uncertain');
    assert.equal(f.sent.length, 1);
});
test('pending record survives lost process and blocks duplicate sends', () => {
    const f = fixture(); f.properties['report.' + id] = JSON.stringify({ state: 'pending', time: Date.now() });
    assert.equal(f.post(f.payload()).error, 'uncertain');
    assert.equal(f.sent.length, 0);
});
for (const scenario of ['quota', 'daily', 'burst', 'disabled', 'locked', 'retained']) {
    test('admission bound: ' + scenario, () => {
        const f = fixture();
        if (scenario === 'quota') { f.state.quota = 0; }
        if (scenario === 'daily' || scenario === 'burst') {
            f.properties.budget = JSON.stringify({ start: Date.now(), count: scenario === 'daily' ? 20 : 0,
                burstStart: Date.now(), burstCount: scenario === 'burst' ? 5 : 0 });
        }
        if (scenario === 'disabled') { f.properties.REPORTS_ENABLED = 'false'; }
        if (scenario === 'locked') { f.state.locked = true; }
        if (scenario === 'retained') {
            for (let i = 0; i < 200; i++) { f.properties['report.' + i] = JSON.stringify({ state: 'sent', time: Date.now() }); }
        }
        assert.equal(f.post(f.payload()).ok, false);
        assert.equal(f.sent.length, 0);
    });
}
for (const scenario of ['uuid', 'crlf', 'recipient', 'blank', 'long', 'metadata', 'base64', 'magic', 'size', 'noncanonical']) {
    test('validation rejects ' + scenario, () => {
        const f = fixture(); const p = f.payload();
        if (scenario === 'uuid') { p.report.id = 'bad'; }
        if (scenario === 'crlf') { p.report.email = 'a@example.com\r\nBcc: attacker@example.com'; }
        if (scenario === 'recipient') { p.to = 'attacker@example.com'; }
        if (scenario === 'blank') { p.report.message = ' '; }
        if (scenario === 'long') { p.report.message = 'a'.repeat(8001); }
        if (scenario === 'metadata') { p.report.metadata.os = 'macOS\nInjected'; }
        if (scenario === 'base64') { p.archive = 'bad*'; }
        if (scenario === 'magic') { p.archive = Buffer.alloc(24).toString('base64'); }
        if (scenario === 'size') { p.archive = 'A'.repeat(Math.ceil(8 * 1024 * 1024 / 3) * 4 + 4); }
        if (scenario === 'noncanonical') { p.archive = p.archive.slice(0, -2) + 'B='; }
        assert.equal(f.post(p).ok, false);
        assert.equal(f.sent.length, 0);
    });
}
test('expired deduplication entries are removed; only bounded metadata is retained', () => {
    const f = fixture(); f.properties['report.old'] = JSON.stringify({ state: 'sent', time: Date.now() - 8 * 86400000 });
    assert.equal(f.post(f.payload()).ok, true);
    assert.equal(f.properties['report.old'], undefined);
});
test('health probe and disable function never send email', () => {
    const f = fixture(); f.context.disableReports();
    assert.deepEqual(JSON.parse(f.context.doGet().text), { service: 'DeadLockyReports', enabled: false });
    assert.equal(f.sent.length, 0);
});
