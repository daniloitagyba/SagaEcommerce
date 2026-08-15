import { useEffect, useRef, useState } from 'react';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Paper from '@mui/material/Paper';
import IconButton from '@mui/material/IconButton';
import TextField from '@mui/material/TextField';
import Button from '@mui/material/Button';
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutlineOutlined';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import Alert from '@mui/material/Alert';
import { RequireAuth } from '../components/RequireAuth';
import { useCart, useClearCart, useRemoveCartItem, useUpdateCartItem } from '../api/cart';
import { formatMoney } from '../format';
import type { CartLineItem } from '../api/types';

// Typing "10" into the quantity field used to fire one PUT per keystroke
// (qty=1, then qty=10 moments later), with no debounce and no in-flight
// guard - whichever response landed last won the query-cache write, not
// whichever request was sent last. This collapses a burst of keystrokes
// into a single commit after the shopper pauses.
const QUANTITY_COMMIT_DELAY_MILLISECONDS = 400;

function QuantityField({
  item,
  onCommit,
}: {
  item: CartLineItem;
  onCommit: (sku: string, quantity: number) => void;
}) {
  const [value, setValue] = useState(item.quantity);
  const commitTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    setValue(item.quantity);
  }, [item.quantity]);

  useEffect(
    () => () => {
      if (commitTimeoutRef.current) {
        clearTimeout(commitTimeoutRef.current);
      }
    },
    [],
  );

  return (
    <TextField
      type="number"
      size="small"
      value={value}
      onChange={(event) => {
        const next = Math.max(1, Number(event.target.value));
        setValue(next);
        if (commitTimeoutRef.current) {
          clearTimeout(commitTimeoutRef.current);
        }
        commitTimeoutRef.current = setTimeout(() => {
          onCommit(item.sku, next);
        }, QUANTITY_COMMIT_DELAY_MILLISECONDS);
      }}
      slotProps={{ htmlInput: { min: 1, style: { textAlign: 'center' } } }}
      sx={{ width: 80 }}
    />
  );
}

function CartContents() {
  const { data: cart, isLoading } = useCart();
  const updateCartItem = useUpdateCartItem();
  const removeCartItem = useRemoveCartItem();
  const clearCart = useClearCart();
  const navigate = useNavigate();

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (!cart || cart.items.length === 0) {
    return (
      <Stack spacing={2} sx={{ alignItems: 'flex-start' }}>
        <Typography>Your cart is empty.</Typography>
        <Button component={RouterLink} to="/" variant="contained">
          Browse products
        </Button>
      </Stack>
    );
  }

  return (
    <Stack spacing={3}>
      <TableContainer component={Paper} variant="outlined">
        <Table>
          <TableHead>
            <TableRow>
              <TableCell>Product</TableCell>
              <TableCell align="right">Unit price</TableCell>
              <TableCell align="center">Quantity</TableCell>
              <TableCell align="right">Subtotal</TableCell>
              <TableCell />
            </TableRow>
          </TableHead>
          <TableBody>
            {cart.items.map((item) => (
              <TableRow key={item.sku}>
                <TableCell>{item.productName}</TableCell>
                <TableCell align="right">{formatMoney(item.unitPrice, item.currency)}</TableCell>
                <TableCell align="center">
                  <QuantityField
                    item={item}
                    onCommit={(sku, quantity) => updateCartItem.mutate({ sku, quantity })}
                  />
                </TableCell>
                <TableCell align="right">{formatMoney(item.unitPrice * item.quantity, item.currency)}</TableCell>
                <TableCell align="right">
                  <IconButton
                    aria-label={`remove ${item.productName}`}
                    onClick={() => removeCartItem.mutate(item.sku)}
                  >
                    <DeleteOutlineIcon />
                  </IconButton>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>

      <Stack direction="row" spacing={2} sx={{ justifyContent: 'flex-end', alignItems: 'center' }}>
        {/* useClearCart previously had no caller anywhere in the app - the backend
            already clears the cart server-side post-checkout, but a shopper had no
            way to abandon a cart mid-shop short of removing every line one at a time. */}
        <Button
          variant="text"
          color="inherit"
          onClick={() => clearCart.mutate()}
          loading={clearCart.isPending}
          disabled={clearCart.isPending}
        >
          Clear cart
        </Button>
        <Typography variant="h6">Total: {formatMoney(cart.total, cart.currency)}</Typography>
        <Button variant="contained" size="large" onClick={() => navigate('/checkout')}>
          Checkout
        </Button>
      </Stack>

      {cart.expiresInSeconds != null && cart.expiresInSeconds < 300 && (
        <Alert severity="warning">Your cart will expire soon due to inactivity.</Alert>
      )}
    </Stack>
  );
}

export function CartPage() {
  return (
    <RequireAuth>
      <Stack spacing={3}>
        <Typography variant="h4" component="h1">
          Your cart
        </Typography>
        <CartContents />
      </Stack>
    </RequireAuth>
  );
}
