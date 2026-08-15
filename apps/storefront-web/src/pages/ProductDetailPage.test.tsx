import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import { useProductSummary } from '../api/catalog';
import { useCart, useUpdateCartItem } from '../api/cart';
import { ProductDetailPage } from './ProductDetailPage';
import type { Cart, Product, ProductSummary } from '../api/types';

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../api/catalog', () => ({
  useProductSummary: vi.fn(),
}));

vi.mock('../api/cart', () => ({
  useCart: vi.fn(),
  useUpdateCartItem: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedUseProductSummary = vi.mocked(useProductSummary);
const mockedUseCart = vi.mocked(useCart);
const mockedUseUpdateCartItem = vi.mocked(useUpdateCartItem);

const sampleProduct: Product = {
  id: 'p1',
  name: 'Widget',
  description: 'A widget.',
  categorySlug: 'electronics',
  price: 10,
  currency: 'BRL',
  sku: 'SKU1',
  attributes: {},
  images: [],
  createdAt: '2026-01-01T00:00:00Z',
};

const sampleSummary: ProductSummary = {
  product: sampleProduct,
  inventory: { sku: 'SKU1', availability: 'InStock' },
  degraded: false,
};

function renderProductDetailPage() {
  return render(
    <MemoryRouter initialEntries={['/products/SKU1']}>
      <Routes>
        <Route path="/products/:sku" element={<ProductDetailPage />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('ProductDetailPage', () => {
  beforeEach(() => {
    mockedUseAuth.mockReturnValue({
      isAuthenticated: true,
      signinRedirect: vi.fn(),
    } as never);
    mockedUseProductSummary.mockReturnValue({
      data: sampleSummary,
      isLoading: false,
      isError: false,
    } as never);
  });

  it('adds to an empty cart using the page-local quantity', () => {
    const mutate = vi.fn();
    mockedUseCart.mockReturnValue({ data: undefined } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate, isPending: false } as never);

    renderProductDetailPage();

    fireEvent.click(screen.getByRole('button', { name: 'Add to cart' }));

    expect(mutate).toHaveBeenCalledWith(
      { sku: 'SKU1', quantity: 1 },
      expect.objectContaining({ onSuccess: expect.any(Function) as unknown }),
    );
  });

  it('adds to the existing cart quantity instead of overwriting it', () => {
    const mutate = vi.fn();
    const cartWithExistingItem: Cart = {
      items: [{ sku: 'SKU1', quantity: 3, unitPrice: 10, currency: 'BRL', productName: 'Widget', addedAt: '2026-01-01T00:00:00Z' }],
      total: 30,
      currency: 'BRL',
      expiresInSeconds: null,
      version: 1,
    };
    mockedUseCart.mockReturnValue({ data: cartWithExistingItem } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate, isPending: false } as never);

    renderProductDetailPage();

    fireEvent.click(screen.getByRole('button', { name: 'Add to cart' }));

    // A shopper with 3 of SKU1 already in their cart clicking "Add to
    // cart" (page-local quantity defaults to 1) must end up with 4, not 1 -
    // PUT /cart/carts/me/items/{sku} sets the quantity absolutely.
    expect(mutate).toHaveBeenCalledWith(
      { sku: 'SKU1', quantity: 4 },
      expect.objectContaining({ onSuccess: expect.any(Function) as unknown }),
    );
  });

  it('redirects to sign-in instead of adding to cart when unauthenticated', () => {
    const mutate = vi.fn();
    const signinRedirect = vi.fn();
    mockedUseAuth.mockReturnValue({ isAuthenticated: false, signinRedirect } as never);
    mockedUseCart.mockReturnValue({ data: undefined } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate, isPending: false } as never);

    renderProductDetailPage();

    fireEvent.click(screen.getByRole('button', { name: 'Add to cart' }));

    expect(signinRedirect).toHaveBeenCalled();
    expect(mutate).not.toHaveBeenCalled();
  });
});
