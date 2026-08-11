import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { useAuth } from 'react-oidc-context';
import { useCart, useRemoveCartItem, useUpdateCartItem } from '../api/cart';
import { formatMoney } from '../format';
import { CartPage } from './CartPage';
import type { Cart } from '../api/types';

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../api/cart', () => ({
  useCart: vi.fn(),
  useUpdateCartItem: vi.fn(),
  useRemoveCartItem: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedUseCart = vi.mocked(useCart);
const mockedUseUpdateCartItem = vi.mocked(useUpdateCartItem);
const mockedUseRemoveCartItem = vi.mocked(useRemoveCartItem);

const sampleCart: Cart = {
  items: [
    { sku: 'SKU1', quantity: 2, unitPrice: 10, currency: 'BRL', productName: 'Widget', addedAt: '2026-01-01T00:00:00Z' },
  ],
  total: 20,
  currency: 'BRL',
  expiresInSeconds: null,
  version: 1,
};

function renderCartPage() {
  return render(
    <MemoryRouter initialEntries={['/cart']}>
      <Routes>
        <Route path="/cart" element={<CartPage />} />
        <Route path="/checkout" element={<div>Checkout page</div>} />
        <Route path="/" element={<div>Catalog page</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('CartPage', () => {
  beforeEach(() => {
    mockedUseAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      activeNavigator: undefined,
      signinRedirect: vi.fn(),
    } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate: vi.fn() } as never);
    mockedUseRemoveCartItem.mockReturnValue({ mutate: vi.fn() } as never);
  });

  it('shows an empty-cart message with a link back to the catalog', () => {
    mockedUseCart.mockReturnValue({ data: undefined, isLoading: false } as never);

    renderCartPage();

    expect(screen.getByText('Your cart is empty.')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('link', { name: 'Browse products' }));
    expect(screen.getByText('Catalog page')).toBeInTheDocument();
  });

  it('lists items and navigates to checkout', () => {
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);

    renderCartPage();

    expect(screen.getByText('Widget')).toBeInTheDocument();
    const totalRow = screen.getByText(/Total:/);
    expect(totalRow.textContent).toBe(`Total: ${formatMoney(sampleCart.total, sampleCart.currency)}`);

    fireEvent.click(screen.getByRole('button', { name: 'Checkout' }));
    expect(screen.getByText('Checkout page')).toBeInTheDocument();
  });

  it('updates the quantity through useUpdateCartItem when the field changes', () => {
    const mutate = vi.fn();
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate } as never);

    renderCartPage();

    fireEvent.change(screen.getByDisplayValue('2'), { target: { value: '5' } });

    expect(mutate).toHaveBeenCalledWith({ sku: 'SKU1', quantity: 5 });
  });

  it('never sends a quantity below 1', () => {
    const mutate = vi.fn();
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseUpdateCartItem.mockReturnValue({ mutate } as never);

    renderCartPage();

    fireEvent.change(screen.getByDisplayValue('2'), { target: { value: '0' } });

    expect(mutate).toHaveBeenCalledWith({ sku: 'SKU1', quantity: 1 });
  });

  it('removes an item through useRemoveCartItem', () => {
    const mutate = vi.fn();
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseRemoveCartItem.mockReturnValue({ mutate } as never);

    renderCartPage();

    fireEvent.click(screen.getByRole('button', { name: 'remove Widget' }));

    expect(mutate).toHaveBeenCalledWith('SKU1');
  });

  it('warns when the cart is about to expire', () => {
    mockedUseCart.mockReturnValue({
      data: { ...sampleCart, expiresInSeconds: 120 },
      isLoading: false,
    } as never);

    renderCartPage();

    expect(screen.getByText('Your cart will expire soon due to inactivity.')).toBeInTheDocument();
  });

  it('does not warn when the cart has plenty of time left', () => {
    mockedUseCart.mockReturnValue({
      data: { ...sampleCart, expiresInSeconds: 900 },
      isLoading: false,
    } as never);

    renderCartPage();

    expect(screen.queryByText('Your cart will expire soon due to inactivity.')).not.toBeInTheDocument();
  });
});
