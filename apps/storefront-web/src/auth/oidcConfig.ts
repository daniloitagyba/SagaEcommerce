import type { AuthProviderProps } from 'react-oidc-context';

// Public client, PKCE (S256) - response_type "code" is oidc-client-ts's
// default and it uses PKCE automatically for it, so there's nothing else
// to configure for the code-exchange half. orders-storefront's own
// redirectUris/webOrigins (scripts/keycloak-configure-realm.sh) must
// include this app's origin or the redirect back from Keycloak 400s.
export const oidcConfig: AuthProviderProps = {
  authority: `${import.meta.env.VITE_KEYCLOAK_URL}/realms/${import.meta.env.VITE_KEYCLOAK_REALM}`,
  client_id: import.meta.env.VITE_KEYCLOAK_CLIENT_ID,
  redirect_uri: window.location.origin,
  post_logout_redirect_uri: window.location.origin,
  scope: 'openid profile',
  onSigninCallback: () => {
    // Strips ?code=&state= back off the URL after the redirect completes -
    // oidc-client-ts needs them once to exchange the code, a shopper
    // reloading the page or copying the URL should never see them again.
    window.history.replaceState({}, document.title, window.location.pathname);
  },
};
