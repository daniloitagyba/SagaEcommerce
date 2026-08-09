# storefront-web

The shopper-facing frontend: React + Vite + TypeScript, Material UI for
components, Tailwind for layout utilities (Preflight disabled - MUI's own
`CssBaseline` is the reset in use, see `src/index.css`). Talks to
`Storefront.Service`'s BFF at `/api/*` and logs shoppers in against Keycloak
directly (PKCE, `react-oidc-context`).

## Pages

| Route             | Page                | Auth required |
| ------------------ | -------------------- | ------------- |
| `/`                 | Catalog (browse/filter by category) | no |
| `/products/:sku`   | Product detail, add to cart | no |
| `/cart`            | Cart | yes |
| `/checkout`        | Checkout, handles the 409 price-mismatch retry flow | yes |
| `/orders`          | Order history | yes |
| `/orders/:id`      | Order detail - cancel, request a return | yes |
| `*`                | Not found | no |

Every route past the landing page is code-split (`React.lazy` in `App.tsx`).

## Running it

```bash
npm install
cp .env.example .env   # see below
npm run dev             # http://localhost:5173, proxies /api/* to VITE_BFF_URL
```

```bash
npm run lint    # oxlint
npm run build   # tsc -b && vite build - typecheck + production build
npm test        # vitest
```

## Environment variables

Vite inlines `VITE_*` variables at build time - see `.env.example`.

- `VITE_BFF_URL` - dev only. `vite.config.ts` proxies `/api/*` to this
  origin so the dev server behaves like the same-origin deployment
  `Storefront.Service` serves in production (it has no CORS middleware by
  design - see its own `Program.cs`/README). Not needed in production: the
  built app is served *from* `Storefront.Service`, so `/api/*` is already
  same-origin.
- `VITE_KEYCLOAK_URL` / `VITE_KEYCLOAK_REALM` / `VITE_KEYCLOAK_CLIENT_ID` -
  the realm and public (PKCE, no secret) client a shopper's browser logs
  into. Must match `scripts/keycloak-configure-realm.sh`'s `realm_name` and
  `storefront_client_id`. In the Docker Compose deployment these are baked
  in as build args on `storefront-service` in `compose/compose.yaml`, not
  read from this app's own `.env` (which only matters for `npm run dev`).

## Production build

`apps/src/Storefront.Service/Dockerfile` builds this app in its own Node
stage and copies `dist/` into the service's `wwwroot`, so the compiled
frontend and the BFF API ship as one image, one origin, no CORS. Running
`Storefront.Service` outside Docker (`dotnet run`) serves no frontend at
all - there's no `wwwroot` in source control on purpose, so nothing stale
can silently get served instead of a real build.
