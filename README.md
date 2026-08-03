[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Sabnzbd.Runners.OpenApiClient/build-and-test.yml?style=for-the-badge)](https://github.com/soenneker/Soenneker.Sabnzbd.Runners.OpenApiClient/actions/workflows/build-and-test.yml)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/Soenneker.Sabnzbd.Runners.OpenApiClient/daily-automatic-update.yml?style=for-the-badge&label=Daily%20Update)](https://github.com/soenneker/Soenneker.Sabnzbd.Runners.OpenApiClient/actions/workflows/daily-automatic-update.yml)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Sabnzbd.Runners.OpenApiClient
### A runner that regenerates and updates Soenneker.Sabnzbd.OpenApiClient.

This runner executes a GitHub action that updates another project. It's not meant for consumption.

Every run downloads the official [SABnzbd 5.0 API reference](https://sabnzbd.org/wiki/configuration/5.0/api), parses its function tables, request examples, input-parameter tables, and JSON response examples, and builds a fresh `openapi.json`. The runner then normalizes that generated document, generates the Kiota client, builds the result, and pushes changes.

Because SABnzbd selects operations with `mode` and `name` query parameters rather than distinct HTTP paths, the generated document exposes one GET operation with the complete discovered query surface and a combined response envelope inferred from the documented JSON examples. File uploads are represented separately as the endpoint's multipart POST operation.
