import { useEffect } from 'react';
import { useAuth } from 'react-oidc-context';
import { setAccessToken, setSigninRedirectCallback } from './tokenStore';

/** Bridges react-oidc-context's user state (and its signinRedirect action) into the plain module-level token store api/client.ts's interceptor reads from and calls on a 401. Renders nothing. */
export function TokenSync() {
  const auth = useAuth();

  useEffect(() => {
    setAccessToken(auth.user?.access_token ?? null);
  }, [auth.user?.access_token]);

  useEffect(() => {
    setSigninRedirectCallback(() => void auth.signinRedirect());
    return () => setSigninRedirectCallback(null);
  }, [auth]);

  return null;
}
