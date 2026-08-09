import AppBar from '@mui/material/AppBar';
import Toolbar from '@mui/material/Toolbar';
import Typography from '@mui/material/Typography';
import Button from '@mui/material/Button';
import IconButton from '@mui/material/IconButton';
import Badge from '@mui/material/Badge';
import Container from '@mui/material/Container';
import ShoppingCartIcon from '@mui/icons-material/ShoppingCart';
import ReceiptLongIcon from '@mui/icons-material/ReceiptLong';
import { Link as RouterLink, Outlet } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import { useCart } from '../api/cart';

export function Layout() {
  const auth = useAuth();
  const { data: cart } = useCart();
  const itemCount = cart?.items.reduce((sum, item) => sum + item.quantity, 0) ?? 0;

  return (
    <div className="flex min-h-full flex-col">
      <AppBar position="static" color="default" elevation={0} sx={{ borderBottom: 1, borderColor: 'divider' }}>
        <Toolbar>
          <Typography
            variant="h6"
            component={RouterLink}
            to="/"
            sx={{ flexGrow: 1, textDecoration: 'none', color: 'inherit', fontWeight: 600 }}
          >
            SagaEcommerce
          </Typography>

          {auth.isAuthenticated && (
            <IconButton component={RouterLink} to="/orders" aria-label="order history" sx={{ mr: 1 }}>
              <ReceiptLongIcon />
            </IconButton>
          )}

          <IconButton component={RouterLink} to="/cart" aria-label="cart" sx={{ mr: 2 }}>
            <Badge badgeContent={itemCount} color="secondary">
              <ShoppingCartIcon />
            </Badge>
          </IconButton>

          {auth.isAuthenticated ? (
            <div className="flex items-center gap-3">
              <Typography variant="body2" color="text.secondary">
                {auth.user?.profile.preferred_username}
              </Typography>
              <Button onClick={() => auth.signoutRedirect()}>Sign out</Button>
            </div>
          ) : (
            <Button variant="contained" onClick={() => auth.signinRedirect()}>
              Sign in
            </Button>
          )}
        </Toolbar>
      </AppBar>

      <Container component="main" maxWidth="lg" sx={{ flexGrow: 1, py: 4 }}>
        <Outlet />
      </Container>
    </div>
  );
}
