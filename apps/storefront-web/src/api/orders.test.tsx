import { describe, expect, it, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import {
  useCancelOrder,
  useCheckout,
  useOrder,
  useOrderHistory,
  useOrderSummaries,
  useReturnOrder,
} from './orders';
import type { CreateReturnRequest, Order } from './types';

// Standalone hoisted bindings, not member accesses off a mocked apiClient
// object - axios's AxiosInstance methods are typed as relying on `this`,
// so `apiClient.get`/`.post` referenced as values (even inside
// expect(...).toHaveBeenCalledWith) trips oxlint's unbound-method rule.
const { mockGet, mockPost } = vi.hoisted(() => ({
  mockGet: vi.fn(),
  mockPost: vi.fn(),
}));

vi.mock('./client', () => ({
  apiClient: { get: mockGet, post: mockPost },
}));

const sampleOrder: Order = {
  id: 'order-1',
  customerId: 'customer-1',
  amount: 49.9,
  currency: 'BRL',
  status: 'Created',
  createdAt: '2026-01-01T00:00:00Z',
  correlationId: 'corr-1',
  instanceId: 'instance-1',
  pricing: null,
  paymentMethod: 'Pix',
};

function makeWrapper() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return {
    queryClient,
    wrapper: ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
    ),
  };
}

describe('useCheckout', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('posts the checkout request and returns the created order', async () => {
    mockPost.mockResolvedValue({ data: sampleOrder });
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useCheckout(), { wrapper });
    result.current.mutate({ paymentMethod: 'Pix' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockPost).toHaveBeenCalledWith('/storefront/checkout', { paymentMethod: 'Pix' });
    expect(result.current.data).toEqual(sampleOrder);
  });

  it('marks the cached cart stale on a successful checkout', async () => {
    mockPost.mockResolvedValue({ data: sampleOrder });
    const { wrapper, queryClient } = makeWrapper();
    queryClient.setQueryData(['cart'], { items: [], total: 0 });
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCheckout(), { wrapper });
    result.current.mutate({ paymentMethod: 'Pix' });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['cart'] });
  });
});

describe('useOrder', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('fetches an order by id', async () => {
    mockGet.mockResolvedValue({ data: sampleOrder });
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useOrder('order-1'), { wrapper });

    await waitFor(() => expect(result.current.data).toEqual(sampleOrder));
    expect(mockGet).toHaveBeenCalledWith('/orders/order-1');
  });

  it('does not fetch when no id is given', () => {
    const { wrapper } = makeWrapper();

    renderHook(() => useOrder(undefined), { wrapper });

    expect(mockGet).not.toHaveBeenCalled();
  });
});

describe('useOrderHistory', () => {
  it('fetches the order history for an id', async () => {
    mockGet.mockResolvedValue({ data: { orderId: 'order-1', snapshot: null, events: [] } });
    const { wrapper } = makeWrapper();

    const { result } = renderHook(() => useOrderHistory('order-1'), { wrapper });

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(mockGet).toHaveBeenCalledWith('/orders/order-1/history');
  });
});

describe('useOrderSummaries', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('requests without a cursor param on the first page', async () => {
    mockGet.mockResolvedValue({ data: { items: [], nextCursor: null } });
    const { wrapper } = makeWrapper();

    renderHook(() => useOrderSummaries(), { wrapper });

    await waitFor(() => expect(mockGet).toHaveBeenCalled());
    expect(mockGet).toHaveBeenCalledWith('/orders/summary', { params: undefined });
  });

  it('forwards the cursor when paginating', async () => {
    mockGet.mockResolvedValue({ data: { items: [], nextCursor: null } });
    const { wrapper } = makeWrapper();

    renderHook(() => useOrderSummaries('cursor-123'), { wrapper });

    await waitFor(() => expect(mockGet).toHaveBeenCalled());
    expect(mockGet).toHaveBeenCalledWith('/orders/summary', { params: { cursor: 'cursor-123' } });
  });
});

describe('useCancelOrder', () => {
  it('posts a cancellation and invalidates the order, history, and summaries caches', async () => {
    mockPost.mockResolvedValue({ data: { orderId: 'order-1', status: 'Cancelled', correlationId: 'corr-1' } });
    const { wrapper, queryClient } = makeWrapper();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');

    const { result } = renderHook(() => useCancelOrder(), { wrapper });
    result.current.mutate('order-1');

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockPost).toHaveBeenCalledWith('/orders/order-1/cancellation', {});
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['order', 'order-1'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['order-history', 'order-1'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['order-summaries'] });
  });
});

describe('useReturnOrder', () => {
  it('posts the return request and invalidates the order and history caches', async () => {
    mockPost.mockResolvedValue({ data: { returnId: 'return-1' } });
    const { wrapper, queryClient } = makeWrapper();
    const invalidateSpy = vi.spyOn(queryClient, 'invalidateQueries');
    const request: CreateReturnRequest = { items: [{ sku: 'SKU1', quantity: 1 }] };

    const { result } = renderHook(() => useReturnOrder(), { wrapper });
    result.current.mutate({ id: 'order-1', request });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockPost).toHaveBeenCalledWith('/orders/order-1/returns', request);
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['order', 'order-1'] });
    expect(invalidateSpy).toHaveBeenCalledWith({ queryKey: ['order-history', 'order-1'] });
  });
});
