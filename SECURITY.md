# Security policy

## Supported versions

Vela is pre-1.0 and ships as two release lines (see [`RELEASING.md`](RELEASING.md)). Security fixes go into the
**latest released version of each line** only; there are no backports to older minors.

| Line | Packages | Supported |
| --- | --- | --- |
| Framework | `Vela.Messages`, `Vela.Plugins`, `Vela.Testing` | latest release (`0.42.x` at the time of writing) |
| CLI | `Vela.Cli` | latest release (`0.29.x` at the time of writing) |

Upgrade to the latest release of a line before reporting, when you can, to check the issue is still there.

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a security vulnerability.**

Report it privately through GitHub's private vulnerability reporting: on the repository, open the **Security** tab
and choose **Report a vulnerability**. The report is visible only to the maintainer until an advisory is published.

Please include, as far as you can:

- the affected package(s) and version(s) (`Vela.Plugins`, `Vela.Testing`, `Vela.Cli`, ...);
- what an attacker can do, and under which conditions (for example a plugin registered in a given way, a CLI
  command run against a given environment);
- the steps or a minimal project to reproduce it;
- any suggested fix or mitigation.

Do not include real credentials, connection strings, tokens or customer data in the report.

## What to expect

Vela is maintained by one person on a best-effort basis, so there is no guaranteed response time. You should get an
acknowledgement once the report has been read, and updates as it is investigated. When a fix is released, it is
recorded in [`CHANGELOG.md`](CHANGELOG.md) and, where appropriate, in a GitHub security advisory, crediting you
unless you prefer not to be named.

Issues in Microsoft Dataverse, the Power Platform or the Microsoft SDKs Vela builds on are not Vela vulnerabilities:
report them to Microsoft through the [Microsoft Security Response Center](https://msrc.microsoft.com/).
