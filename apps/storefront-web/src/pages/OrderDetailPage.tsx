import { useState } from 'react';
import { useParams, useLocation } from 'react-router-dom';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Paper from '@mui/material/Paper';
import Chip from '@mui/material/Chip';
import Divider from '@mui/material/Divider';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Button from '@mui/material/Button';
import Alert from '@mui/material/Alert';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import { RequireAuth } from '../components/RequireAuth';
import { ReturnDialog } from '../components/ReturnDialog';
import { useCancelOrder, useOrder, useOrderHistory } from '../api/orders';
import { formatMoney, formatDateTime } from '../format';
import { statusColor, isSelfServiceCancellable, isReturnable } from '../orderStatus';
import { describeApiError } from '../api/client';

function OrderDetail({ orderId }: { orderId: string }) {
  const location = useLocation();
  const justPlaced = (location.state as { justPlaced?: boolean } | null)?.justPlaced ?? false;
  const { data: order, isLoading, isError } = useOrder(orderId);
  const { data: history } = useOrderHistory(orderId);
  const cancelOrder = useCancelOrder();
  const [returnDialogOpen, setReturnDialogOpen] = useState(false);

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (isError || !order) {
    return <Alert severity="error">This order could not be found.</Alert>;
  }

  return (
    <Stack spacing={3}>
      {justPlaced && <Alert severity="success">Your order was placed successfully.</Alert>}

      <Stack direction="row" sx={{ justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <Typography variant="h4" component="h1">
            Order {order.id.slice(0, 8)}
          </Typography>
          <Typography color="text.secondary">Placed {formatDateTime(order.createdAt)}</Typography>
        </div>
        <Chip label={order.status} color={statusColor(order.status)} />
      </Stack>

      {cancelOrder.isError && <Alert severity="error">{describeApiError(cancelOrder.error)}</Alert>}

      <Stack direction="row" spacing={2}>
        {isSelfServiceCancellable(order.status) && (
          <Button
            variant="outlined"
            color="error"
            onClick={() => cancelOrder.mutate(order.id)}
            loading={cancelOrder.isPending}
          >
            Cancel order
          </Button>
        )}
        {isReturnable(order.status) && order.pricing && (
          <Button variant="outlined" onClick={() => setReturnDialogOpen(true)}>
            Request return
          </Button>
        )}
      </Stack>

      {order.pricing && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Typography variant="h6" gutterBottom>
            Items
          </Typography>
          <Table>
            <TableHead>
              <TableRow>
                <TableCell>Product</TableCell>
                <TableCell align="right">Qty</TableCell>
                <TableCell align="right">Unit price</TableCell>
                <TableCell align="right">Discount</TableCell>
                <TableCell align="right">Total</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {order.pricing.lines.map((line) => (
                <TableRow key={line.sku}>
                  <TableCell>{line.productName}</TableCell>
                  <TableCell align="right">{line.quantity}</TableCell>
                  <TableCell align="right">{formatMoney(line.unitPrice, order.currency)}</TableCell>
                  <TableCell align="right">
                    {line.lineDiscount > 0 ? `-${formatMoney(line.lineDiscount, order.currency)}` : '—'}
                  </TableCell>
                  <TableCell align="right">{formatMoney(line.lineTotal, order.currency)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>

          <Divider sx={{ my: 2 }} />

          <Stack spacing={0.5} sx={{ maxWidth: 280, ml: 'auto' }}>
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography variant="body2">Subtotal</Typography>
              <Typography variant="body2">{formatMoney(order.pricing.subtotal, order.currency)}</Typography>
            </Stack>
            {order.pricing.discountTotal > 0 && (
              <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
                <Typography variant="body2">Discount {order.pricing.couponCode && `(${order.pricing.couponCode})`}</Typography>
                <Typography variant="body2">-{formatMoney(order.pricing.discountTotal, order.currency)}</Typography>
              </Stack>
            )}
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography variant="body2">Shipping</Typography>
              <Typography variant="body2">{formatMoney(order.pricing.shippingTotal, order.currency)}</Typography>
            </Stack>
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography variant="body2">Tax</Typography>
              <Typography variant="body2">{formatMoney(order.pricing.taxTotal, order.currency)}</Typography>
            </Stack>
            <Divider />
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography sx={{ fontWeight: 600 }}>Total</Typography>
              <Typography sx={{ fontWeight: 600 }}>{formatMoney(order.amount, order.currency)}</Typography>
            </Stack>
          </Stack>
        </Paper>
      )}

      {history && history.events.length > 0 && (
        <Paper variant="outlined" sx={{ p: 3 }}>
          <Typography variant="h6" gutterBottom>
            Timeline
          </Typography>
          <Stack spacing={1}>
            {history.events.map((event) => (
              <Stack key={event.id} direction="row" sx={{ justifyContent: 'space-between' }}>
                <Typography variant="body2">{event.eventType}</Typography>
                <Typography variant="body2" color="text.secondary">
                  {formatDateTime(event.occurredAt)}
                </Typography>
              </Stack>
            ))}
          </Stack>
        </Paper>
      )}

      {order.pricing && (
        <ReturnDialog open={returnDialogOpen} onClose={() => setReturnDialogOpen(false)} order={order} />
      )}
    </Stack>
  );
}

export function OrderDetailPage() {
  const { id } = useParams<{ id: string }>();
  if (!id) {
    return <Alert severity="error">Missing order id.</Alert>;
  }
  return (
    <RequireAuth>
      <OrderDetail orderId={id} />
    </RequireAuth>
  );
}
