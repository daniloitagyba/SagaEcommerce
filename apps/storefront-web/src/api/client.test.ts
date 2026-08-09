import { describe, expect, it } from 'vitest';
import { AxiosError } from 'axios';
import { describeApiError, isPriceMismatch } from './client';
import type { ProblemDetails } from './types';

function axiosErrorWith(status: number, data: ProblemDetails): AxiosError<ProblemDetails> {
  return new AxiosError('request failed', undefined, undefined, undefined, {
    status,
    data,
    statusText: '',
    headers: {},
    config: {} as never,
  });
}

describe('isPriceMismatch', () => {
  it('is true for a 409 with the Price Changed title', () => {
    const error = axiosErrorWith(409, {
      title: 'Price Changed',
      expectedSubtotal: 100,
      actualSubtotal: 120,
    });
    expect(isPriceMismatch(error)).toBe(true);
  });

  it('is false for a 409 with a different title', () => {
    const error = axiosErrorWith(409, { title: 'Coupon Already Redeemed' });
    expect(isPriceMismatch(error)).toBe(false);
  });

  it('is false for a non-Axios error', () => {
    expect(isPriceMismatch(new Error('boom'))).toBe(false);
  });
});

describe('describeApiError', () => {
  it('joins validation errors when present', () => {
    const error = axiosErrorWith(400, {
      errors: { line1: ['Address is required'], postalCode: ['Invalid format'] },
    });
    expect(describeApiError(error)).toBe('Address is required Invalid format');
  });

  it('falls back to detail when there are no field errors', () => {
    const error = axiosErrorWith(404, { detail: 'Order not found' });
    expect(describeApiError(error)).toBe('Order not found');
  });

  it('falls back to title when there is no detail', () => {
    const error = axiosErrorWith(500, { title: 'Internal Server Error' });
    expect(describeApiError(error)).toBe('Internal Server Error');
  });

  it('falls back to a generic message for a non-Axios error', () => {
    expect(describeApiError(new Error('boom'))).toBe('Something went wrong. Please try again.');
  });
});
