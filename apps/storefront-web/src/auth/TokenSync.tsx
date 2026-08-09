import { useEffect } from 'react';
import { useAuth } from 'react-oidc-context';
import { setAccessToken } from './tokenStore';

/** Bridges react-oidc-context's user state into the plain module-level token store api/client.ts's interceptor reads from. Renders nothing. */
export function TokenSync() {
  const auth = useAuth();

  useEffect(() => {
    setAccessToken(auth.user?.access_token ?? null);
  }, [auth.user?.access_token]);

  return null;
}
