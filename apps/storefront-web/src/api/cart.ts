import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { apiClient } from './client';
import type { Cart } from './types';
import { useAuth } from 'react-oidc-context';

const CART_QUERY_KEY = ['cart'];

async function getCart(): Promise<Cart> {
  const { data } = await apiClient.get<Cart>('/cart/carts/me');
  return data;
}

export function useCart() {
  const auth = useAuth();
  return useQuery({
    queryKey: CART_QUERY_KEY,
    queryFn: getCart,
    // Every /api/cart/* route requires a signed-in shopper (Cart.Service
    // has no anonymous carts) - asking before that just produces a 401
    // this hook would otherwise have to swallow on every unauthenticated page.
    enabled: auth.isAuthenticated,
  });
}

async function updateCartItem(sku: string, quantity: number): Promise<Cart> {
  const { data } = await apiClient.put<Cart>(`/cart/carts/me/items/${encodeURIComponent(sku)}`, { quantity });
  return data;
}

export function useUpdateCartItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ sku, quantity }: { sku: string; quantity: number }) => updateCartItem(sku, quantity),
    onSuccess: (cart) => queryClient.setQueryData(CART_QUERY_KEY, cart),
  });
}

async function removeCartItem(sku: string): Promise<void> {
  await apiClient.delete(`/cart/carts/me/items/${encodeURIComponent(sku)}`);
}

export function useRemoveCartItem() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (sku: string) => removeCartItem(sku),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CART_QUERY_KEY }),
  });
}

async function clearCart(): Promise<void> {
  await apiClient.delete('/cart/carts/me');
}

export function useClearCart() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: clearCart,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CART_QUERY_KEY }),
  });
}
