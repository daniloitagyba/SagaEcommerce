import type { OrderStatus } from './api/types';

type ChipColor = 'default' | 'primary' | 'secondary' | 'success' | 'warning' | 'error' | 'info';

export function statusColor(status: OrderStatus): ChipColor {
  switch (status) {
    case 'Delivered':
    case 'Confirmed':
      return 'success';
    case 'Shipped':
    case 'Picking':
      return 'info';
    case 'Backordered':
    case 'FulfillmentHold':
      return 'warning';
    case 'Cancelled':
    case 'Returned':
      return 'error';
    default:
      return 'default';
  }
}

// Mirrors AdvanceFulfillmentHandler.SelfServiceCancellableFrom on the
// backend - kept here only to decide whether to show the button at all;
// the backend's own compare-and-set is still what actually decides, this
// is just so a shopper doesn't see a cancel button that would 409.
export function isSelfServiceCancellable(status: OrderStatus): boolean {
  return status === 'Created' || status === 'Confirmed' || status === 'Backordered';
}

export function isReturnable(status: OrderStatus): boolean {
  return status === 'Delivered';
}
