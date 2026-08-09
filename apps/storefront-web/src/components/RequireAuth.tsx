import { useEffect } from 'react';
import { useAuth } from 'react-oidc-context';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import type { ReactNode } from 'react';

/** Redirects straight to Keycloak rather than showing a "please sign in" page - every route this wraps (cart, checkout, orders) is useless to an anonymous shopper anyway. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const auth = useAuth();

  useEffect(() => {
    if (!auth.isLoading && !auth.isAuthenticated && !auth.activeNavigator) {
      void auth.signinRedirect();
    }
  }, [auth]);

  if (!auth.isAuthenticated) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  return <>{children}</>;
}
