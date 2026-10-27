# Demo REST API protected by Frontegg (per-app audience)

Built from `../frontegg-demo-api-spec.md`. Proves that a service which is its own
Frontegg application accepts **only** tokens whose `aud` is its own app ID, and that
assigning a user to the app in Frontegg is what grants access.

| Project | What |
|---|---|
| `DemoApi/` | ASP.NET Core minimal API, plain `JwtBearer`, no Frontegg SDK. Listens on `http://localhost:5080`. |
| `DemoApi.Tests/` | xUnit + `WebApplicationFactory`. No network: a test RSA key replaces Frontegg's JWKS. |
| `TokenHelper/` | Console app: auth code + PKCE against Frontegg, prints a real access token to stdout. |

Requires the .NET 10 SDK.

## Configuration

`DemoApi/appsettings.json` holds the issuer and a `<API_APP_ID>` placeholder. The API
**refuses to start** until the audience is set:

```bash
export Frontegg__Audience=<API_APP_ID>    # the qauth-demo-api app ID from Frontegg
```

No secrets anywhere: the API only validates tokens. `TokenHelper` reads the app's client
secret from `FE_APP_SECRET`, or prompts for it without echo.

## Frontegg set-up

Spec §3, done in the portal before the manual tests. Until §3 step 2 is done (hosted-login
redirect `http://localhost:8765/callback`), Frontegg answers the helper's authorize request
with **404 "Redirect uri wasn't found"**.

## Endpoints

| Method | Path | Needs | Returns |
|---|---|---|---|
| GET | `/healthz` | nothing | `200 {"ok":true}` |
| GET | `/whoami` | valid token | Q.auth claims, `aud`, `iss`, `exp`, all claim names |
| GET | `/portfolios/{id}` | + `q_tenant` | the portfolio if it's the caller's tenant, else 404 |
| GET | `/admin/ping` | + `q_tenant` + role `Admin` | `200 {"admin":true}`, else 403 |

A valid token without `q_tenant` gets `403 {"error":"missing_tenant"}` everywhere except
`/whoami`. That's what Frontegg silently issues when the targeting rule doesn't match.

A refused token gets a 401 whose `WWW-Authenticate` header says why, and the reason is
logged at warning level. The token itself is never logged.

## Docker

GitHub Actions (`.github/workflows/build.yml`) builds and tests the solution, then builds
`DemoApi` as a multi-arch image (`linux/amd64`, `linux/arm64`). Pushes to `main` publish
`operry/docker-testapi:<run number>` and `:latest` to Docker Hub. Pull requests only build.
The workflow needs the repo secrets `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`.

In the container the API listens on port 8080 and still refuses to start without an audience.
The `Makefile` wraps the local equivalents (`make help` lists them):

```bash
make test                                   # build + test, as CI does
make run AUDIENCE=<API_APP_ID>              # dotnet run on :5080
make docker-build
make docker-run AUDIENCE=<API_APP_ID>       # container on :8080
curl -s localhost:8080/healthz
make token APP_ID=<API_APP_ID>              # TokenHelper; secret from FE_APP_SECRET or prompted
```

## Automated tests

```bash
dotnet test
```

Covers every row of spec §6.1, plus startup refusal on an empty or placeholder audience.

Result, 2026-10-26: **21 passed, 0 failed.**

## Manual tests with real tokens

```bash
export Frontegg__Audience=<API_APP_ID>
dotnet run --project DemoApi &            # listens on :5080

# 1. API app token for testuser1 -> accepted
export FE_APP_SECRET=…                     # API app's secret; don't put it in a file
T=$(dotnet run --project TokenHelper -- --app-id <API_APP_ID>)
curl -s localhost:5080/whoami -H "authorization: Bearer $T" | jq
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/portfolios/acme-1  -H "authorization: Bearer $T"   # 200
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/portfolios/other-1 -H "authorization: Bearer $T"   # 404
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/admin/ping        -H "authorization: Bearer $T"   # 200 (testuser1 is Admin)

# 2. vmcp app token for the SAME user -> refused on aud
export FE_APP_SECRET=…                     # vmcp app's secret
V=$(dotnet run --project TokenHelper -- --app-id 99396ec4-1f3c-4a64-a41b-71110a4f8ae9)
curl -s -D- -o /dev/null localhost:5080/whoami -H "authorization: Bearer $V" | grep -i -E '^HTTP|www-authenticate'   # 401, audience invalid

# 3. test3 (not assigned to the API app): open https://app-4a5u5lob7bvi.frontegg.com/oauth/logout,
#    then run the helper with the API app ID and sign in as test3 -> Frontegg refuses the login.

# 4. Expired: keep $T, wait 6 minutes, call /whoami again -> 401 (lifetime 300 s)
curl -s -D- -o /dev/null localhost:5080/whoami -H "authorization: Bearer $T" | grep -i -E '^HTTP|www-authenticate'

unset FE_APP_SECRET
```

`TokenHelper` prints only the access token on stdout; `aud`, `applicationId`, `q_tenant`,
`roles` and `exp` go to stderr, with a warning if `q_tenant` is missing. To switch user,
sign out at `…/oauth/logout` first, or Frontegg silently reuses the last session.

### Results

Not run yet. Fill in after the Frontegg set-up, then copy into `../frontegg-setup-checklist.md`
as a new step.

| Step | Expected | Observed |
|---|---|---|
| 1 `/whoami` | `aud` = `applicationId` = API app ID, `q_tenant` = `d3275653-…`, `roles` = `["Admin"]`, `caller` = `user` | |
| 1 `acme-1` / `other-1` / `admin/ping` | 200 / 404 / 200 | |
| 2 vmcp token | 401, `The audience '99396ec4-…' is invalid` | |
| 3 test3 sign-in | refused by Frontegg, no token | |
| 4 after 6 min | 401, token expired | |

Checked against the live environment without a real token, 2026-10-26:
- The API fetches Frontegg's discovery and JWKS (key `9ce64ee6`) and refuses a forged RS256
  token with `401 invalid_token "The signature is invalid"`.
- The helper's authorize URL is correct: with an already-registered redirect URI, Frontegg
  answers 302 to its login page. With `localhost:8765` not yet registered, it answers 404
  "Redirect uri wasn't found".

## Notes

- The issuer check is the library default, which is already an exact, ordinal string match.
  A test confirms that `https://…frontegg.com/` (trailing slash) is refused.
- `roles` as a plain string (`"Admin"`) is read as a one-element list. That's tolerated,
  not relied on: a templated multi-value array would arrive as one string.
- JSON responses use relaxed escaping so `o'test2@peek.org.uk` comes back as written rather
  than `o'test2…`.


 
