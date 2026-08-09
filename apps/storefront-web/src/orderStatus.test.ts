import { describe, expect, it } from 'vitest';
import { isReturnable, isSelfServiceCancellable, statusColor } from './orderStatus';
import type { OrderStatus } from './api/types';

describe('statusColor', () => {
  it.each<[OrderStatus, string]>([
    ['Delivered', 'success'],
    ['Confirmed', 'success'],
    ['Shipped', 'info'],
    ['Picking', 'info'],
    ['Backordered', 'warning'],
    ['FulfillmentHold', 'warning'],
    ['Cancelled', 'error'],
    ['Returned', 'error'],
    ['Created', 'default'],
  ])('maps %s to %s', (status, expected) => {
    expect(statusColor(status)).toBe(expected);
  });
});

describe('isSelfServiceCancellable', () => {
  it.each<OrderStatus>(['Created', 'Confirmed', 'Backordered'])(
    'is true for %s',
    (status) => {
      expect(isSelfServiceCancellable(status)).toBe(true);
    },
  );

  it.each<OrderStatus>(['Shipped', 'Delivered', 'Cancelled', 'Returned'])(
    'is false for %s',
    (status) => {
      expect(isSelfServiceCancellable(status)).toBe(false);
    },
  );
});

describe('isReturnable', () => {
  it('is true only for Delivered', () => {
    expect(isReturnable('Delivered')).toBe(true);
    expect(isReturnable('Shipped')).toBe(false);
    expect(isReturnable('Cancelled')).toBe(false);
  });
});
