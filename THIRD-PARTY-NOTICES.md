# Third-Party Notices

The Genealogy.Workspace product is licensed under the MIT License (see
`LICENSE` in this directory). It depends on the following
third-party components, each under its own permissive license. This file is
provided for attribution; it is not an exhaustive transitive dependency list.

## .NET (NuGet) dependencies

| Component | Version | License | Project |
|---|---|---|---|
| Npgsql | 10.0.3 | PostgreSQL License (permissive, BSD/MIT-style) | https://github.com/npgsql/npgsql |
| dbup-postgresql | 7.0.1 | MIT License | https://github.com/DbUp/DbUp |
| ModelContextProtocol | 1.1.0 | MIT License | https://github.com/modelcontextprotocol/csharp-sdk |
| Microsoft.Extensions.Hosting | 8.0.0 | MIT License | https://github.com/dotnet/runtime |
| xUnit (`xunit`, `xunit.runner.visualstudio`) — test-only | 2.9.x / 3.x | Apache-2.0 / MIT | https://github.com/xunit/xunit |
| Microsoft.NET.Test.Sdk — test-only | 18.0.x | MIT License | https://github.com/microsoft/vstest |

The .NET runtime and SDK themselves are distributed by Microsoft under the MIT
License (https://github.com/dotnet/runtime).

## Python

The GEDCOM tooling in `tools/gedcom/` (`gedcom_tool.py`) uses only the Python
standard library (Python Software Foundation License). No third-party Python
packages are required.

## Runtime image

The workspace runs PostgreSQL via the official `postgres:17` Docker image
(PostgreSQL License). The image is pulled at run time and is not redistributed
as part of this repository.

---

All of the licenses above are permissive and compatible with the MIT License
under which this product is released. Refer to each project's repository for the
full license text.
