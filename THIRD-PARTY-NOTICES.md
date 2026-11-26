# Third-party notices

AJBrowser includes or uses the following third-party software.

## uBlock Origin Lite (bundled)

- **What:** the MV3 content blocker AJBrowser installs into its browser engine to block ads and trackers.
- **Version:** 2026.1124.1811
- **File:** `Extensions/uBOLite_2026.1124.1811.chromium.zip`. This is the official release file, shipped **unmodified**.
  SHA-256: `b0472c07028ced638cc0ae7c57403dd95ad6cdfc7391a37f9fa7aaf6239dfc55`
- **Author:** Raymond Hill and contributors
- **License:** GNU General Public License v3.0. The full license text is `LICENSE.txt` inside the zip.
- **Release downloads:** https://github.com/uBlockOrigin/uBOL-home/releases
- **Source code:** https://github.com/gorhill/uBlock (build steps are in `README.md` inside the zip)

uBlock Origin Lite is a separate program that AJBrowser only loads (aggregation). AJBrowser's own code is not
derived from it. Its filter rulesets come from the filter lists its authors credit (EasyList, EasyPrivacy, uBlock
filters, Peter Lowe's list and others), each under its own license.

## Microsoft Edge WebView2

- **Packages:** `Microsoft.Web.WebView2` (NuGet). The WebView2 Runtime is installed separately on Windows.
- **License:** see https://www.nuget.org/packages/Microsoft.Web.WebView2/ (Microsoft license terms)

## CommunityToolkit.Mvvm

- **License:** MIT, © .NET Foundation and Contributors. https://github.com/CommunityToolkit/dotnet
