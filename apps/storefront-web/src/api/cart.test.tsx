import { describe, expect, it, vi, beforeEach } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useAuth } from 'react-oidc-context';
import type { ReactNode } from 'react';
import { useCart, useClearCart, useRemoveCartItem, useUpdateCartItem } from './cart';
import type { Cart } from './types';

// Standalone hoisted bindings, not member accesses off a mocked apiClient
// object - axios's AxiosInstance methods are typed as relying on `this`,
// so `apiClient.get`/`.put`/`.delete` referenced as values (even inside
// expect(...).toHaveBeenCalledWith) trips oxlint's unbound-method rule.
const { mockGet, mockPut, mockDelete } = vi.hoisted(() => ({
  mockGet: vi.fn(),
  mockPut: vi.fn(),
  mockDelete: vi.fn(),
}));

vi.mock('./client', () => ({
  apiClient: { get: mockGet, put: mockPut, delete: mockDelete },
}));

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);

function wrapper({ children }: { children: ReactNode }) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
}

const sampleCart: Cart = {
  items: [
    { sku: 'SKU1', quantity: 2, unitPrice: 10, currency: 'BRL', productName: 'Widget', addedAt: '2026-01-01T00:00:00Z' },
  ],
  total: 20,
  currency: 'BRL',
  expiresInSeconds: null,
  version: 1,
};

describe('useCart', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('does not fetch when the shopper is not authenticated', () => {
    mockedUseAuth.mockReturnValue({ isAuthenticated: false } as never);

    renderHook(() => useCart(), { wrapper });

    expect(mockGet).not.toHaveBeenCalled();
  });

  it('fetches the cart once authenticated', async () => {
    mockedUseAuth.mockReturnValue({ isAuthenticated: true } as never);
    mockGet.mockResolvedValue({ data: sampleCart });

    const { result } = renderHook(() => useCart(), { wrapper });

    await waitFor(() => expect(result.current.data).toEqual(sampleCart));
    expect(mockGet).toHaveBeenCalledWith('/cart/carts/me');
  });
});

describe('useUpdateCartItem', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('PUTs the new quantity for the given sku', async () => {
    mockPut.mockResolvedValue({ data: sampleCart });

    const { result } = renderHook(() => useUpdateCartItem(), { wrapper });
    result.current.mutate({ sku: 'SKU1', quantity: 3 });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockPut).toHaveBeenCalledWith('/cart/carts/me/items/SKU1', { quantity: 3 });
  });

  it('URL-encodes a sku with special characters', async () => {
    mockPut.mockResolvedValue({ data: sampleCart });

    const { result } = renderHook(() => useUpdateCartItem(), { wrapper });
    result.current.mutate({ sku: 'SKU/1 A', quantity: 1 });

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockPut).toHaveBeenCalledWith('/cart/carts/me/items/SKU%2F1%20A', { quantity: 1 });
  });
});

describe('useRemoveCartItem', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('DELETEs the item by sku', async () => {
    mockDelete.mockResolvedValue({ data: undefined });

    const { result } = renderHook(() => useRemoveCartItem(), { wrapper });
    result.current.mutate('SKU1');

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockDelete).toHaveBeenCalledWith('/cart/carts/me/items/SKU1');
  });
});

describe('useClearCart', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('DELETEs the whole cart', async () => {
    mockDelete.mockResolvedValue({ data: undefined });

    const { result } = renderHook(() => useClearCart(), { wrapper });
    result.current.mutate();

    await waitFor(() => expect(result.current.isSuccess).toBe(true));
    expect(mockDelete).toHaveBeenCalledWith('/cart/carts/me');
  });
});
