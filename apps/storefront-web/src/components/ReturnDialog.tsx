import { useState } from 'react';
import Dialog from '@mui/material/Dialog';
import DialogTitle from '@mui/material/DialogTitle';
import DialogContent from '@mui/material/DialogContent';
import DialogActions from '@mui/material/DialogActions';
import Button from '@mui/material/Button';
import Stack from '@mui/material/Stack';
import FormControlLabel from '@mui/material/FormControlLabel';
import Checkbox from '@mui/material/Checkbox';
import TextField from '@mui/material/TextField';
import MenuItem from '@mui/material/MenuItem';
import Alert from '@mui/material/Alert';
import type { Order } from '../api/types';
import type { ReturnReasonCategory } from '../api/types';
import { useReturnOrder } from '../api/orders';
import { describeApiError } from '../api/client';

interface ReturnDialogProps {
  open: boolean;
  onClose: () => void;
  order: Order;
}

export function ReturnDialog({ open, onClose, order }: ReturnDialogProps) {
  const returnOrder = useReturnOrder();
  const lines = order.pricing?.lines ?? [];
  const [selected, setSelected] = useState<Set<string>>(new Set(lines.map((line) => line.sku)));
  const [reasonCategory, setReasonCategory] = useState<ReturnReasonCategory>('Unwanted');
  const [reason, setReason] = useState('');

  const toggle = (sku: string) => {
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(sku)) {
        next.delete(sku);
      } else {
        next.add(sku);
      }
      return next;
    });
  };

  const submit = () => {
    const items = lines
      .filter((line) => selected.has(line.sku))
      .map((line) => ({ sku: line.sku, quantity: line.quantity }));

    returnOrder.mutate(
      { id: order.id, request: { items, reason: reason || undefined, reasonCategory } },
      { onSuccess: onClose },
    );
  };

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>Request a return</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ pt: 1 }}>
          {lines.map((line) => (
            <FormControlLabel
              key={line.sku}
              control={<Checkbox checked={selected.has(line.sku)} onChange={() => toggle(line.sku)} />}
              label={`${line.productName} (x${line.quantity})`}
            />
          ))}

          <TextField
            select
            label="Reason"
            value={reasonCategory}
            onChange={(event) => setReasonCategory(event.target.value as ReturnReasonCategory)}
          >
            <MenuItem value="Unwanted">No longer wanted</MenuItem>
            <MenuItem value="Regret">Changed my mind</MenuItem>
            <MenuItem value="Defect">Item is defective</MenuItem>
          </TextField>

          <TextField
            label="Additional details (optional)"
            multiline
            minRows={2}
            value={reason}
            onChange={(event) => setReason(event.target.value)}
          />

          {returnOrder.isError && <Alert severity="error">{describeApiError(returnOrder.error)}</Alert>}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>Cancel</Button>
        <Button
          variant="contained"
          onClick={submit}
          loading={returnOrder.isPending}
          disabled={selected.size === 0}
        >
          Submit return
        </Button>
      </DialogActions>
    </Dialog>
  );
}
