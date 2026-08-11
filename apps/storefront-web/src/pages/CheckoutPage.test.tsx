import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter, Routes, Route, useLocation, useParams } from 'react-router-dom';
import { AxiosError } from 'axios';
import { useAuth } from 'react-oidc-context';
import { useCart } from '../api/cart';
import { useCheckout } from '../api/orders';
import { formatMoney } from '../format';
import { CheckoutPage } from './CheckoutPage';
import type { Cart, Order, ProblemDetails } from '../api/types';

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

vi.mock('../api/cart', () => ({
  useCart: vi.fn(),
}));

vi.mock('../api/orders', () => ({
  useCheckout: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);
const mockedUseCart = vi.mocked(useCart);
const mockedUseCheckout = vi.mocked(useCheckout);

const sampleCart: Cart = {
  items: [
    { sku: 'SKU1', quantity: 2, unitPrice: 10, currency: 'BRL', productName: 'Widget', addedAt: '2026-01-01T00:00:00Z' },
  ],
  total: 20,
  currency: 'BRL',
  expiresInSeconds: null,
  version: 1,
};

const sampleOrder: Order = {
  id: 'order-1',
  customerId: 'customer-1',
  amount: 20,
  currency: 'BRL',
  status: 'Created',
  createdAt: '2026-01-01T00:00:00Z',
  correlationId: 'corr-1',
  instanceId: 'instance-1',
  pricing: null,
  paymentMethod: 'Pix',
};

function axiosErrorWith(status: number, data: ProblemDetails): AxiosError<ProblemDetails> {
  return new AxiosError('request failed', undefined, undefined, undefined, {
    status,
    data,
    statusText: '',
    headers: {},
    config: {} as never,
  });
}

function OrderPlacedProbe() {
  const { id } = useParams();
  const location = useLocation();
  const justPlaced = Boolean((location.state as { justPlaced?: boolean } | null)?.justPlaced);
  return <div>Placed order {id}, justPlaced={String(justPlaced)}</div>;
}

function renderCheckoutPage() {
  return render(
    <MemoryRouter initialEntries={['/checkout']}>
      <Routes>
        <Route path="/checkout" element={<CheckoutPage />} />
        <Route path="/orders/:id" element={<OrderPlacedProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('CheckoutPage', () => {
  beforeEach(() => {
    mockedUseAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      activeNavigator: undefined,
      signinRedirect: vi.fn(),
    } as never);
  });

  it('shows an alert instead of a form when the cart is empty', () => {
    mockedUseCart.mockReturnValue({ data: { ...sampleCart, items: [] }, isLoading: false } as never);
    mockedUseCheckout.mockReturnValue({ mutate: vi.fn(), isPending: false, isError: false, error: null } as never);

    renderCheckoutPage();

    expect(screen.getByText('Your cart is empty - add something before checking out.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Place order' })).not.toBeInTheDocument();
  });

  it('places the order with the default payment method and navigates to the order page', () => {
    const mutate = vi.fn((_request, options: { onSuccess?: (order: Order) => void }) => {
      options.onSuccess?.(sampleOrder);
    });
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseCheckout.mockReturnValue({ mutate, isPending: false, isError: false, error: null } as never);

    renderCheckoutPage();
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(mutate).toHaveBeenCalledWith(
      { couponCode: undefined, paymentMethod: 'Pix', shippingAddress: undefined },
      expect.any(Object),
    );
    expect(screen.getByText('Placed order order-1, justPlaced=true')).toBeInTheDocument();
  });

  it('uppercases the coupon code and includes it in the request', () => {
    const mutate = vi.fn();
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseCheckout.mockReturnValue({ mutate, isPending: false, isError: false, error: null } as never);

    renderCheckoutPage();
    fireEvent.change(screen.getByLabelText('Coupon code'), { target: { value: 'save10' } });
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));

    expect(mutate).toHaveBeenCalledWith(
      expect.objectContaining({ couponCode: 'SAVE10' }),
      expect.any(Object),
    );
  });

  it('shows a price-mismatch alert with an accept-and-retry action', () => {
    const mismatch = axiosErrorWith(409, { title: 'Price Changed', expectedSubtotal: 20, actualSubtotal: 25 });
    const mutate = vi.fn((_request, options: { onError?: (error: unknown) => void }) => {
      options.onError?.(mismatch);
    });
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseCheckout.mockReturnValue({ mutate, isPending: false, isError: false, error: null } as never);

    renderCheckoutPage();
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));

    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('The price changed since you last viewed your cart');
    expect(alert.textContent).toContain(formatMoney(20, 'BRL'));
    expect(alert.textContent).toContain(formatMoney(25, 'BRL'));

    fireEvent.click(screen.getByRole('button', { name: 'Accept & retry' }));
    expect(mutate).toHaveBeenCalledTimes(2);
  });

  it('shows a generic error message for any other failure', () => {
    mockedUseCart.mockReturnValue({ data: sampleCart, isLoading: false } as never);
    mockedUseCheckout.mockReturnValue({
      mutate: vi.fn(),
      isPending: false,
      isError: true,
      error: axiosErrorWith(503, { detail: 'PostgreSQL is currently unavailable.' }),
    } as never);

    renderCheckoutPage();

    expect(screen.getByText('PostgreSQL is currently unavailable.')).toBeInTheDocument();
  });
});
