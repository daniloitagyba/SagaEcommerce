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
import { useCart, useRemoveCartItem, useUpdateCartItem } from '../api/cart';
import { formatMoney } from '../format';

function CartContents() {
  const { data: cart, isLoading } = useCart();
  const updateCartItem = useUpdateCartItem();
  const removeCartItem = useRemoveCartItem();
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
                  <TextField
                    type="number"
                    size="small"
                    value={item.quantity}
                    onChange={(event) => {
                      const next = Math.max(1, Number(event.target.value));
                      updateCartItem.mutate({ sku: item.sku, quantity: next });
                    }}
                    slotProps={{ htmlInput: { min: 1, style: { textAlign: 'center' } } }}
                    sx={{ width: 80 }}
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
