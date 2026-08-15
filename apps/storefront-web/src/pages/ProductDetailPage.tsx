import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Chip from '@mui/material/Chip';
import Button from '@mui/material/Button';
import TextField from '@mui/material/TextField';
import Alert from '@mui/material/Alert';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import Snackbar from '@mui/material/Snackbar';
import { useProductSummary } from '../api/catalog';
import { useCart, useUpdateCartItem } from '../api/cart';
import { formatMoney } from '../format';
import type { Availability } from '../api/types';

const AVAILABILITY_LABEL: Record<Availability, { label: string; color: 'success' | 'warning' | 'error' }> = {
  InStock: { label: 'In stock', color: 'success' },
  Low: { label: 'Low stock', color: 'warning' },
  OutOfStock: { label: 'Out of stock', color: 'error' },
};

export function ProductDetailPage() {
  const { sku } = useParams<{ sku: string }>();
  const auth = useAuth();
  const { data, isLoading, isError } = useProductSummary(sku);
  const cart = useCart();
  const updateCartItem = useUpdateCartItem();
  const [quantity, setQuantity] = useState(1);
  const [added, setAdded] = useState(false);

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (isError || !data?.product) {
    return <Alert severity="error">This product could not be found.</Alert>;
  }

  const { product, inventory, degraded } = data;
  const availability = inventory ? AVAILABILITY_LABEL[inventory.availability] : null;

  const handleAddToCart = () => {
    if (!auth.isAuthenticated) {
      void auth.signinRedirect();
      return;
    }
    // PUT /cart/carts/me/items/{sku} sets the line's quantity absolutely
    // (CartEndpoints.cs: `existing.WithQuantity(request.Quantity)`) - sending
    // the page-local quantity alone would overwrite whatever the shopper
    // already had in their cart for this SKU instead of adding to it.
    const existingQuantity = cart.data?.items.find((item) => item.sku === product.sku)?.quantity ?? 0;
    updateCartItem.mutate(
      { sku: product.sku, quantity: existingQuantity + quantity },
      { onSuccess: () => setAdded(true) },
    );
  };

  return (
    <Stack direction={{ xs: 'column', md: 'row' }} spacing={4}>
      <Box sx={{ flex: 1 }}>
        {product.images[0] ? (
          <img
            src={product.images[0]}
            alt={product.name}
            className="w-full rounded-lg object-cover"
          />
        ) : (
          <div className="flex h-80 items-center justify-center rounded-lg bg-gray-100 text-gray-400">
            No image
          </div>
        )}
      </Box>

      <Stack spacing={2} sx={{ flex: 1 }}>
        <Chip label={product.categorySlug} size="small" sx={{ alignSelf: 'flex-start' }} />
        <Typography variant="h4" component="h1">
          {product.name}
        </Typography>
        <Typography variant="h5" sx={{ fontWeight: 600 }}>
          {formatMoney(product.price, product.currency)}
        </Typography>

        {availability && <Chip label={availability.label} color={availability.color} sx={{ alignSelf: 'flex-start' }} />}
        {degraded && (
          <Alert severity="warning">Stock information is temporarily unavailable for this item.</Alert>
        )}

        <Typography color="text.secondary">{product.description}</Typography>

        {Object.entries(product.attributes).length > 0 && (
          <Stack spacing={0.5}>
            {Object.entries(product.attributes).map(([key, value]) => (
              <Typography key={key} variant="body2" color="text.secondary">
                <strong>{key}:</strong> {value}
              </Typography>
            ))}
          </Stack>
        )}

        <Stack direction="row" spacing={2} sx={{ alignItems: 'center', pt: 2 }}>
          <TextField
            type="number"
            label="Quantity"
            size="small"
            value={quantity}
            onChange={(event) => setQuantity(Math.max(1, Number(event.target.value)))}
            slotProps={{ htmlInput: { min: 1 } }}
            sx={{ width: 100 }}
          />
          <Button
            variant="contained"
            size="large"
            onClick={handleAddToCart}
            loading={updateCartItem.isPending}
            disabled={inventory?.availability === 'OutOfStock'}
          >
            Add to cart
          </Button>
        </Stack>
      </Stack>

      <Snackbar
        open={added}
        autoHideDuration={2500}
        onClose={() => setAdded(false)}
        message={`Added ${product.name} to cart`}
      />
    </Stack>
  );
}
