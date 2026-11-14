# Security and data handling

This is unsupported archived software. No security audit or production suitability is promised. Use disposable data and isolated profiles until you have independently reviewed the code.

The public repository intentionally excludes private chats/prompts, original Git history, user databases and profiles, saved search history, bearer endpoint descriptors, personal screenshots, private paths, generated logs and compiled releases. The initial public commit is a fresh source snapshot; it does not preserve private author metadata or reflogs.

Never attach a live profile, index, crash dump, clipboard contents, access token or raw local log to a public issue. Reproduce with synthetic files and redacted details. This retired project has no staffed vulnerability-response service. Do not put unredacted exploitable details or credentials in public issues; a maintainer who resumes the project should configure a private reporting channel first.

For publication checks use `gitleaks dir . --redact=100 --ignore-gitleaks-allow --no-banner` and `gitleaks git . --redact=100 --ignore-gitleaks-allow --no-banner`. Inspect ignored files and package contents independently. A clean scanner result does not prove absence of all sensitive data or vulnerabilities.
