import { useState } from 'react';
import { useNavigate } from 'react-router-dom';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Button from '@mui/material/Button';
import Paper from '@mui/material/Paper';
import Alert from '@mui/material/Alert';
import Divider from '@mui/material/Divider';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import { RequireAuth } from '../components/RequireAuth';
import { useCart } from '../api/cart';
import { useCheckout } from '../api/orders';
import { isPriceMismatch, describeApiError } from '../api/client';
import { formatMoney } from '../format';
import type { PaymentMethod } from '../api/types';

function CheckoutForm() {
  const { data: cart, isLoading: cartLoading } = useCart();
  const checkout = useCheckout();
  const navigate = useNavigate();

  const [couponCode, setCouponCode] = useState('');
  const [paymentMethod, setPaymentMethod] = useState<PaymentMethod>('Pix');
  const [line1, setLine1] = useState('');
  const [city, setCity] = useState('');
  const [region, setRegion] = useState('');
  const [postalCode, setPostalCode] = useState('');
  const [priceMismatch, setPriceMismatch] = useState<{ expected: number; actual: number } | null>(null);

  const submit = () => {
    setPriceMismatch(null);
    checkout.mutate(
      {
        couponCode: couponCode || undefined,
        paymentMethod,
        shippingAddress:
          line1 || city || region || postalCode
            ? { line1: line1 || undefined, city: city || undefined, region: region || undefined, postalCode: postalCode || undefined }
            : undefined,
      },
      {
        onSuccess: (order) => navigate(`/orders/${order.id}`, { state: { justPlaced: true } }),
        onError: (error) => {
          if (isPriceMismatch(error)) {
            const { expectedSubtotal, actualSubtotal } = error.response!.data;
            setPriceMismatch({ expected: expectedSubtotal, actual: actualSubtotal });
          }
        },
      },
    );
  };

  if (cartLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (!cart || cart.items.length === 0) {
    return <Alert severity="info">Your cart is empty - add something before checking out.</Alert>;
  }

  const generalError =
    checkout.isError && !isPriceMismatch(checkout.error) ? describeApiError(checkout.error) : null;

  return (
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={4}>
      <Stack spacing={3} sx={{ flex: 2 }}>
        <Typography variant="h5">Payment &amp; shipping</Typography>

        <TextField
          select
          label="Payment method"
          value={paymentMethod}
          onChange={(event) => setPaymentMethod(event.target.value as PaymentMethod)}
        >
          <MenuItem value="Pix">Pix</MenuItem>
          <MenuItem value="Card">Card</MenuItem>
          <MenuItem value="Boleto">Boleto</MenuItem>
        </TextField>

        <TextField label="Coupon code" value={couponCode} onChange={(event) => setCouponCode(event.target.value.toUpperCase())} />

        <Divider />
        <Typography variant="subtitle1">Shipping address (optional)</Typography>
        <TextField label="Address line" value={line1} onChange={(event) => setLine1(event.target.value)} />
        <Stack direction="row" spacing={2}>
          <TextField label="City" value={city} onChange={(event) => setCity(event.target.value)} fullWidth />
          <TextField label="State" value={region} onChange={(event) => setRegion(event.target.value)} fullWidth />
          <TextField label="Postal code" value={postalCode} onChange={(event) => setPostalCode(event.target.value)} fullWidth />
        </Stack>

        {priceMismatch && (
          <Alert
            severity="warning"
            action={
              <Button color="inherit" size="small" onClick={submit}>
                Accept &amp; retry
              </Button>
            }
          >
            The price changed since you last viewed your cart: expected{' '}
            {formatMoney(priceMismatch.expected, cart.currency)}, now{' '}
            {formatMoney(priceMismatch.actual, cart.currency)}.
          </Alert>
        )}

        {generalError && <Alert severity="error">{generalError}</Alert>}

        <Button variant="contained" size="large" onClick={submit} loading={checkout.isPending}>
          Place order
        </Button>
      </Stack>

      <Paper variant="outlined" sx={{ flex: 1, p: 3, alignSelf: 'flex-start' }}>
        <Typography variant="h6" gutterBottom>
          Order summary
        </Typography>
        <Stack spacing={1}>
          {cart.items.map((item) => (
            <Stack key={item.sku} direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography variant="body2">
                {item.productName} x{item.quantity}
              </Typography>
              <Typography variant="body2">{formatMoney(item.unitPrice * item.quantity, item.currency)}</Typography>
            </Stack>
          ))}
        </Stack>
        <Divider sx={{ my: 2 }} />
        <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
          <Typography sx={{ fontWeight: 600 }}>Total</Typography>
          <Typography sx={{ fontWeight: 600 }}>{formatMoney(cart.total, cart.currency)}</Typography>
        </Stack>
      </Paper>
    </Stack>
  );
}

export function CheckoutPage() {
  return (
    <RequireAuth>
      <Stack spacing={3}>
        <Typography variant="h4" component="h1">
          Checkout
        </Typography>
        <CheckoutForm />
      </Stack>
    </RequireAuth>
  );
}
