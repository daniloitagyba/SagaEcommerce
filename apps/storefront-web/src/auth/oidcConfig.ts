import { InMemoryWebStorage, WebStorageStateStore } from 'oidc-client-ts';
import type { AuthProviderProps } from 'react-oidc-context';

// Required env vars: a missing one would otherwise silently produce an
// authority like "undefined/realms/undefined" instead of failing fast.
const keycloakUrl = import.meta.env.VITE_KEYCLOAK_URL;
const keycloakRealm = import.meta.env.VITE_KEYCLOAK_REALM;
const keycloakClientId = import.meta.env.VITE_KEYCLOAK_CLIENT_ID;

if (!keycloakUrl || !keycloakRealm || !keycloakClientId) {
  throw new Error(
    'Missing required Keycloak configuration: VITE_KEYCLOAK_URL, VITE_KEYCLOAK_REALM and VITE_KEYCLOAK_CLIENT_ID must all be set.',
  );
}

// Public client, PKCE (S256) - response_type "code" is oidc-client-ts's
// default and it uses PKCE automatically for it, so there's nothing else
// to configure for the code-exchange half. orders-storefront's own
// redirectUris/webOrigins (scripts/keycloak-configure-realm.sh) must
// include this app's origin or the redirect back from Keycloak 400s.
export const oidcConfig: AuthProviderProps = {
  authority: `${keycloakUrl}/realms/${keycloakRealm}`,
  client_id: keycloakClientId,
  redirect_uri: window.location.origin,
  post_logout_redirect_uri: window.location.origin,
  scope: 'openid profile',
  // Without this, oidc-client-ts falls back to its documented default of
  // window.sessionStorage and writes the full user object (access token,
  // ID token, profile) there under an "oidc.user:..." key - defeating
  // tokenStore.ts's own stated intent of keeping the token out of any
  // Web Storage that's readable by an XSS payload.
  userStore: new WebStorageStateStore({ store: new InMemoryWebStorage() }),
  onSigninCallback: () => {
    // Strips ?code=&state= back off the URL after the redirect completes -
    // oidc-client-ts needs them once to exchange the code, a shopper
    // reloading the page or copying the URL should never see them again.
    window.history.replaceState({}, document.title, window.location.pathname);
  },
};
