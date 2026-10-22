#!/usr/bin/env python3
"""Probe only doGet health and Google's receipt redirect; never POST or send mail."""
import argparse
import json
import re
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def probe(endpoint):
    if not re.fullmatch(r"https://script\.google\.com/macros/s/[A-Za-z0-9_-]{20,256}/exec", endpoint):
        raise ValueError("Expected the deployed HTTPS /exec URL")
    opener = urllib.request.build_opener(NoRedirect())
    url = endpoint
    for attempt in range(4):
        try:
            response = opener.open(urllib.request.Request(url, method="GET"), timeout=30)
        except urllib.error.HTTPError as error:
            if error.code not in (302, 303) or attempt == 3:
                raise ValueError("Unexpected HTTP status; check anonymous deployment access") from error
            target = error.headers.get("Location", "")
            parsed = urllib.parse.urlsplit(target)
            if (parsed.scheme != "https" or parsed.hostname != "script.googleusercontent.com"
                    or parsed.port not in (None, 443) or parsed.username or parsed.password
                    or parsed.path != "/macros/echo" or parsed.fragment
                    or not urllib.parse.parse_qs(parsed.query).get("user_content_key")):
                raise ValueError("Unexpected redirect (possibly a Google login page)") from error
            print("Verified HTTPS redirect to script.googleusercontent.com/macros/echo (URL key omitted)")
            url = target
            continue
        with response:
            if response.headers.get_content_type() != "application/json":
                raise ValueError("Expected JSON, not a login page")
            data = response.read(8193)
            if len(data) > 8192:
                raise ValueError("Health response exceeds 8 KiB")
            health = json.loads(data)
            if health.get("service") != "DeadLockyReports" or not isinstance(health.get("enabled"), bool):
                raise ValueError("Unexpected health response")
            print("Anonymous health OK; accepting reports:", health["enabled"])
            return
    raise ValueError("Too many redirects")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("endpoint")
    args = parser.parse_args()
    try:
        probe(args.endpoint)
    except (ValueError, OSError) as failure:
        parser.exit(1, str(failure) + "\n")
