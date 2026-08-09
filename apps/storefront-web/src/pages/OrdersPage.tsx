import { Link as RouterLink } from 'react-router-dom';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Table from '@mui/material/Table';
import TableBody from '@mui/material/TableBody';
import TableCell from '@mui/material/TableCell';
import TableContainer from '@mui/material/TableContainer';
import TableHead from '@mui/material/TableHead';
import TableRow from '@mui/material/TableRow';
import Paper from '@mui/material/Paper';
import Chip from '@mui/material/Chip';
import CircularProgress from '@mui/material/CircularProgress';
import Box from '@mui/material/Box';
import Alert from '@mui/material/Alert';
import { RequireAuth } from '../components/RequireAuth';
import { useOrderSummaries } from '../api/orders';
import { formatMoney, formatDateTime } from '../format';
import { statusColor } from '../orderStatus';

function OrdersList() {
  const { data, isLoading, isError } = useOrderSummaries();

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
        <CircularProgress />
      </Box>
    );
  }

  if (isError) {
    return <Alert severity="error">Could not load your orders.</Alert>;
  }

  if (!data || data.items.length === 0) {
    return <Typography color="text.secondary">You have not placed any orders yet.</Typography>;
  }

  return (
    <TableContainer component={Paper} variant="outlined">
      <Table>
        <TableHead>
          <TableRow>
            <TableCell>Order</TableCell>
            <TableCell>Placed</TableCell>
            <TableCell>Status</TableCell>
            <TableCell align="right">Amount</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {data.items.map((order) => (
            <TableRow
              key={order.orderId}
              hover
              component={RouterLink}
              to={`/orders/${order.orderId}`}
              sx={{ textDecoration: 'none', cursor: 'pointer' }}
            >
              <TableCell sx={{ fontFamily: 'monospace' }}>{order.orderId.slice(0, 8)}</TableCell>
              <TableCell>{order.orderCreatedAt ? formatDateTime(order.orderCreatedAt) : '—'}</TableCell>
              <TableCell>
                <Chip label={order.status} size="small" color={statusColor(order.status)} />
              </TableCell>
              <TableCell align="right">
                {order.amount != null && order.currency ? formatMoney(order.amount, order.currency) : '—'}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

export function OrdersPage() {
  return (
    <RequireAuth>
      <Stack spacing={3}>
        <Typography variant="h4" component="h1">
          Your orders
        </Typography>
        <OrdersList />
      </Stack>
    </RequireAuth>
  );
}
