import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type {
  CancellationResponse,
  CheckoutRequest,
  CreateReturnRequest,
  Order,
  OrderHistory,
  OrderSummaryPage,
  ReturnResponse,
} from './types';

// StorefrontEndpoints.CheckoutAsync - reads the cart server-side, prices
// it, and posts to Orders.Api itself; the response (success, the 409
// price-mismatch shape, or a validation problem) is relayed byte-for-byte,
// which is exactly what api/client.ts's isPriceMismatch/describeApiError
// are built to branch on.
async function checkout(request: CheckoutRequest): Promise<Order> {
  const { data } = await apiClient.post<Order>('/storefront/checkout', request);
  return data;
}

export function useCheckout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: checkout,
    onSuccess: () => {
      // Storefront clears the cart server-side on a successful checkout;
      // this just tells the client it's now stale rather than re-fetching
      // eagerly (the order confirmation page has no cart to show anyway).
      queryClient.invalidateQueries({ queryKey: ['cart'] });
    },
  });
}

async function getOrder(id: string): Promise<Order> {
  const { data } = await apiClient.get<Order>(`/orders/${id}`);
  return data;
}

export function useOrder(id: string | undefined) {
  return useQuery({
    queryKey: ['order', id],
    queryFn: () => getOrder(id!),
    enabled: Boolean(id),
  });
}

async function getOrderHistory(id: string): Promise<OrderHistory> {
  const { data } = await apiClient.get<OrderHistory>(`/orders/${id}/history`);
  return data;
}

export function useOrderHistory(id: string | undefined) {
  return useQuery({
    queryKey: ['order-history', id],
    queryFn: () => getOrderHistory(id!),
    enabled: Boolean(id),
  });
}

async function listOrderSummaries(cursor?: string): Promise<OrderSummaryPage> {
  const { data } = await apiClient.get<OrderSummaryPage>('/orders/summary', {
    params: cursor ? { cursor } : undefined,
  });
  return data;
}

export function useOrderSummaries(cursor?: string) {
  return useQuery({
    queryKey: ['order-summaries', cursor ?? null],
    queryFn: () => listOrderSummaries(cursor),
  });
}

async function cancelOrder(id: string): Promise<CancellationResponse> {
  const { data } = await apiClient.post<CancellationResponse>(`/orders/${id}/cancellation`, {});
  return data;
}

export function useCancelOrder() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: cancelOrder,
    onSuccess: (_result, orderId) => {
      queryClient.invalidateQueries({ queryKey: ['order', orderId] });
      queryClient.invalidateQueries({ queryKey: ['order-history', orderId] });
      queryClient.invalidateQueries({ queryKey: ['order-summaries'] });
    },
  });
}

async function returnOrder(id: string, request: CreateReturnRequest): Promise<ReturnResponse> {
  const { data } = await apiClient.post<ReturnResponse>(`/orders/${id}/returns`, request);
  return data;
}

export function useReturnOrder() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ id, request }: { id: string; request: CreateReturnRequest }) => returnOrder(id, request),
    onSuccess: (_result, { id }) => {
      queryClient.invalidateQueries({ queryKey: ['order', id] });
      queryClient.invalidateQueries({ queryKey: ['order-history', id] });
    },
  });
}
