# Demo REST API protected by Frontegg (per-app audience)

Built from `../frontegg-demo-api-spec.md`. Proves that a service which is its own
Frontegg application accepts **only** tokens whose `aud` is its own app ID, and that
assigning a user to the app in Frontegg is what grants access.

| Project | What |
|---|---|
| `DemoApi/` | ASP.NET Core minimal API, plain `JwtBearer`, no Frontegg SDK. Listens on `http://localhost:5080`. |
| `openfga/` | The OpenFGA model for portfolio access (`model.fga`), its seed data and its tests (`store.fga.yaml`). |
| `DemoApi.Tests/` | xUnit + `WebApplicationFactory`. No network: a test RSA key replaces Frontegg's JWKS. |
| `TokenHelper/` | Console app: signs a user in as a **public client** (code + PKCE, no secret), prints a real access token to stdout. |

Requires the .NET 10 SDK.

## Configuration

`DemoApi/appsettings.json` holds the issuer and a `<API_APP_ID>` placeholder. The API
**refuses to start** until the audience is set:

```bash
export Frontegg__Audience=<API_APP_ID>    # the qauth-demo-api app ID from Frontegg
```

No secrets anywhere: the API only validates tokens, and `TokenHelper` is a public client
that needs only the app ID and the user's own Frontegg sign-in.

## Frontegg set-up

Spec §3, done in the portal before the manual tests. Step 2 matters: until
`http://localhost:8765/callback` is an allowed redirect for the app, Frontegg answers the
helper's sign-in with **404 "Redirect uri wasn't found"**.

## Endpoints

| Method | Path | Needs | Returns |
|---|---|---|---|
| GET | `/healthz` | nothing | `200 {"ok":true}` |
| GET | `/whoami` | valid token | Q.auth claims, `aud`, `iss`, `exp`, all claim names |
| GET | `/portfolios` | + `q_tenant` | the portfolios the caller can view (OpenFGA `ListObjects`) |
| GET | `/portfolios/{id}` | + `q_tenant` + `can_view` | the portfolio, else 404 |
| GET | `/portfolios/{id}/permissions` | + `can_view` | `{"view":true,"edit":…,"share":…}`, for a UI's buttons |
| PUT | `/portfolios/{id}` `{"name":"…"}` | + `can_edit` | the renamed portfolio; 403 if the caller can only view it |
| GET | `/portfolios/{id}/access` | + `can_share` | direct grants: `[{"userId":"<sub>","relation":"viewer"}]` |
| PUT | `/portfolios/{id}/access/{sub}` `{"relation":"viewer"}` | + `can_share` | grants `viewer`, `editor` or `owner`, replacing the user's previous grant |
| DELETE | `/portfolios/{id}/access/{sub}` | + `can_share` | `204`; removes the user's grants |
| GET | `/admin/ping` | + `q_tenant` + role `Admin` | `200 {"admin":true}`, else 403 |

A portfolio the caller can't view is a **404**, whether it's in another tenant, doesn't exist
or just isn't shared with them. Once they can view it, a missing `edit` or `share` is a **403**.
If OpenFGA can't answer, `/portfolios` answers **503** `{"error":"authorization_unavailable"}`:
it never allows by default.

A valid token without `q_tenant` gets `403 {"error":"missing_tenant"}` everywhere except
`/whoami`. That's what Frontegg silently issues when the targeting rule doesn't match.

A refused token gets a 401 whose `WWW-Authenticate` header says why, and the reason is
logged at warning level. The token itself is never logged.

## Portfolio access with OpenFGA

### How it fits together

Two layers, checked in this order on every `/portfolios` call:

1. **The token** (as before): valid, has `q_tenant`, and the portfolio belongs to that tenant.
   Roles come from the token's `roles` claim, managed in Frontegg.
2. **OpenFGA**: may *this user* do *this action* on *this portfolio*? The API asks with
   `Check` (one action), `BatchCheck` (the `/permissions` endpoint) or `ListObjects` (the list).

The model (`openfga/model.fga`):

| Relation on a portfolio | Who has it |
|---|---|
| `owner` | users granted `owner` |
| `editor` | users granted `editor`, owners, and **admins of the portfolio's tenant** |
| `viewer` | users granted `viewer`, and every editor |
| `can_view` / `can_edit` / `can_share` | viewer / editor / owner or tenant admin. **The API only asks for these**, so the rules behind them can change in the model without changing code. |

Keys: users are `user:<sub>` (the token's `sub`), tenants are `tenant:<q_tenant>`.

What's stored in OpenFGA and what isn't:
- **Stored:** each portfolio's tenant (`tenant:<id> tenant portfolio:<id>`, seeded from
  `store.fga.yaml`), and the grants made through `/access`.
- **Not stored:** tenant admins. When the token has the `Admin` role, the API adds
  `user:<sub> admin tenant:<q_tenant>` to that one request as a **contextual tuple**. So roles
  stay in Frontegg only, nothing has to be kept in sync, and an Admin from another tenant gets
  nothing here.

### Run it locally

Needs Docker, the [`fga` CLI](https://github.com/openfga/cli) (`brew install openfga/tap/fga`)
and `jq`.

```bash
make fga-up                           # OpenFGA in memory: API on :8090, playground on http://localhost:3000
make fga-init                         # store + model + seed tuples; IDs written to .openfga.env
make run AUDIENCE=<API_APP_ID>        # the Makefile passes .openfga.env to the API
```

Without `make`: `OpenFga__ApiUrl` (default `http://localhost:8090`), `OpenFga__StoreId`, and
optionally `OpenFga__AuthorizationModelId` (empty = the store's latest model) and
`OpenFga__ApiToken` (a preshared key, if the server needs one; environment only). If
`OpenFga__StoreId` isn't set, the API still starts, `/whoami` and `/admin/ping` still work, and
`/portfolios` answers 503. The in-memory store disappears with `make fga-down`.

### Walk-through with real tokens

`testuser1` has the `Admin` role, so it can do everything in its tenant. `o'test2` is a plain
user, so it sees nothing until someone shares a portfolio with it.

```bash
# Sign in as each user (sign out at …/oauth/logout in between).
A=$(make -s token APP_ID=<API_APP_ID>)    # testuser1 (Admin)
U=$(make -s token APP_ID=<API_APP_ID>)    # o'test2 (User)
api() { curl -s -w ' -> %{http_code}\n' "$@"; }

SUB=$(curl -s localhost:5080/whoami -H "authorization: Bearer $U" | jq -r .subject)   # o'test2's user ID

api localhost:5080/portfolios/acme-1 -H "authorization: Bearer $U"                    # 404: not shared yet

# testuser1 shares it read-only
api -X PUT localhost:5080/portfolios/acme-1/access/$SUB -H "authorization: Bearer $A" \
    -H 'content-type: application/json' -d '{"relation":"viewer"}'                     # 200

api localhost:5080/portfolios/acme-1             -H "authorization: Bearer $U"         # 200
api localhost:5080/portfolios/acme-1/permissions -H "authorization: Bearer $U"         # {"view":true,"edit":false,"share":false}
api -X PUT localhost:5080/portfolios/acme-1 -H "authorization: Bearer $U" \
    -H 'content-type: application/json' -d '{"name":"renamed"}'                        # 403: can view, can't edit

# upgrade to editor, then remove access
api -X PUT localhost:5080/portfolios/acme-1/access/$SUB -H "authorization: Bearer $A" \
    -H 'content-type: application/json' -d '{"relation":"editor"}'                     # 200; the PUT above now works
api localhost:5080/portfolios/acme-1/access -H "authorization: Bearer $A"              # who has access
api -X DELETE localhost:5080/portfolios/acme-1/access/$SUB -H "authorization: Bearer $A"   # 204; back to 404
```

Tokens last 300 s, so get fresh ones if a call turns into a 401.

### Looking inside OpenFGA

The `fga` CLI talks to the same store. Point it there once:

```bash
export FGA_API_URL=http://localhost:8090
export FGA_STORE_ID=$(grep StoreId .openfga.env | cut -d= -f2)

fga tuple read --object portfolio:acme-1 --output-format simple-json       # every tuple on acme-1
fga query check user:<sub> can_edit portfolio:acme-1                       # {"allowed":false}
fga query list-objects user:<sub> can_view portfolio                       # what they can see
fga query check user:<sub> can_share portfolio:acme-1 \
  --contextual-tuple "user:<sub> admin tenant:d3275653-4f23-4862-843d-9b8052d5f9af"   # as the API asks for an Admin
fga tuple write  user:<sub> viewer portfolio:acme-1                        # grant by hand
fga tuple delete user:<sub> viewer portfolio:acme-1
```

The playground at http://localhost:3000 shows the model as a graph and lets you run the same
queries.

### Changing the model

1. Edit `openfga/model.fga`.
2. Add cases to the `tests:` in `openfga/store.fga.yaml` and run `make fga-test` (no server
   needed; CI runs it too).
3. `make fga-model` uploads it as a new model version and pins `.openfga.env` to it. Restart
   the API. Models are versioned and never edited in place, so pinning
   `OpenFga__AuthorizationModelId` means a model upload can't change a running API's behaviour
   by surprise.

If a rule changes but the API still asks the same `can_*` question, no code changes.

### Adding a portfolio

A new portfolio needs two tuples, written when the service creates it:
`tenant:<q_tenant> tenant portfolio:<id>` and `user:<creator sub> owner portfolio:<id>`.
Without the first, tenant admins have no access to it; without the second, only tenant admins do.

### Using it from a web app (React)

The browser never talks to OpenFGA: that needs OpenFGA's credentials and would let anyone ask
about, or write, any tuple. The web app calls `GET /portfolios/{id}/permissions` and uses the
answer to show or hide its Edit and Share buttons. The API still checks every action.

### Before this goes further than a demo

- **Postgres** behind OpenFGA instead of memory (`q.auth/08` §9), and a preshared key
  (`OpenFga__ApiToken`).
- **Consistency:** OpenFGA's check cache is off by default. If it's turned on for
  performance, a check made straight after a grant can see the old answer for a moment, and
  that check needs `HIGHER_CONSISTENCY`.
- **Caching in the API:** none yet. Every `/portfolios` call makes one or two OpenFGA calls.
- **Cross-tenant grants:** OpenFGA would accept a grant to a user from another tenant; the
  token's tenant check is what stops it having any effect. Validating the user when granting
  needs a user directory (Frontegg's API), which this demo doesn't call.
- **The model is per service** (`q.auth/05` §5): this one is the demo API's, in its own store.

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
make token APP_ID=<API_APP_ID>              # TokenHelper: browser sign-in, no secret
```

## Automated tests

```bash
dotnet test
make fga-test                         # the OpenFGA model's own tests
make fga-up fga-init test-integration # also the tests against a real OpenFGA (skipped otherwise)
```

CI runs all three: it starts a throwaway OpenFGA in Docker for the integration tests.

Result, 2026-10-27, after adding OpenFGA: **37 passed** (including 3 against a real OpenFGA
v1.22.0), and the model tests: 21 checks and 4 list queries passing.

Covers every row of spec §6.1, plus startup refusal on an empty or placeholder audience.

Result, 2026-10-26: **21 passed, 0 failed.**

## Manual tests with real tokens

```bash
export Frontegg__Audience=<API_APP_ID>
make fga-up fga-init                      # /portfolios needs OpenFGA (see "Portfolio access with OpenFGA")
make run AUDIENCE=$Frontegg__Audience &   # listens on :5080

# 1. API app token for testuser1 -> accepted
T=$(dotnet run --project TokenHelper -- --app-id <API_APP_ID>)
curl -s localhost:5080/whoami -H "authorization: Bearer $T" | jq
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/portfolios/acme-1  -H "authorization: Bearer $T"   # 200
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/portfolios/other-1 -H "authorization: Bearer $T"   # 404
curl -s -o /dev/null -w '%{http_code}\n' localhost:5080/admin/ping        -H "authorization: Bearer $T"   # 200 (testuser1 is Admin)

# 2. vmcp app token for the SAME user -> refused on aud
V=$(dotnet run --project TokenHelper -- --app-id 99396ec4-1f3c-4a64-a41b-71110a4f8ae9)
curl -s -D- -o /dev/null localhost:5080/whoami -H "authorization: Bearer $V" | grep -i -E '^HTTP|www-authenticate'   # 401, audience invalid

# 3. test3 (not assigned to the API app): open https://app-4a5u5lob7bvi.frontegg.com/oauth/logout,
#    then run the helper with the API app ID and sign in as test3 -> Frontegg refuses the login.

# 4. Expired: keep $T, wait 6 minutes, call /whoami again -> 401 (lifetime 300 s)
curl -s -D- -o /dev/null localhost:5080/whoami -H "authorization: Bearer $T" | grep -i -E '^HTTP|www-authenticate'
```

### How TokenHelper signs in

Authorization code + PKCE (S256) with token endpoint auth `none`, i.e. as a **public
client**, the way a CLI or SPA would. No client secret anywhere:

1. Starts a listener on `http://localhost:8765/callback` (Kestrel, IPv4 and IPv6) and opens
   the browser at `/oauth/authorize` with the app ID, the PKCE challenge and a random `state`.
2. The user signs in with their own Frontegg login. App assignment is enforced here: an
   unassigned user is refused ("user is not associated with the application").
3. Frontegg redirects to the listener with a code; the helper checks `state`.
4. `POST /oauth/token` with `grant_type=authorization_code`, `code`, `redirect_uri`,
   `client_id` and `code_verifier`: no `client_secret`.

Not the device flow: Frontegg's hosted login has no page to enter a device code (its
`verification_uri` is an API that needs a signed-in user's bearer token).

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
- 2026-10-27: Frontegg's discovery lists token endpoint auth `none`, and a real sign-in as
  `restapi-user` through app `7d6b55b5-…` got a token **without a client secret**
  (`qauth-poc/docker-testapi-cli-test.sh`). Against the cluster: `/whoami`, `/portfolios/acme-1`,
  `/admin/ping` 200; `/portfolios/other-1` 404; `aud` = `applicationId` = the app ID.

## Notes

- The issuer check is the library default, which is already an exact, ordinal string match.
  A test confirms that `https://…frontegg.com/` (trailing slash) is refused.
- `roles` as a plain string (`"Admin"`) is read as a one-element list. That's tolerated,
  not relied on: a templated multi-value array would arrive as one string.
- JSON responses use relaxed escaping so `o'test2@peek.org.uk` comes back as written rather
  than `o'test2…`.
