import { useQuery } from '@tanstack/react-query';
import { apiClient } from './client';
import type { Category, ProductListResponse, ProductSummary } from './types';

interface ListProductsParams {
  category?: string;
  skip?: number;
  limit?: number;
}

async function listProducts(params: ListProductsParams): Promise<ProductListResponse> {
  const { data } = await apiClient.get<ProductListResponse>('/catalog/products', { params });
  return data;
}

export function useProducts(params: ListProductsParams) {
  return useQuery({
    queryKey: ['products', params],
    queryFn: () => listProducts(params),
  });
}

async function listCategories(): Promise<Category[]> {
  const { data } = await apiClient.get<Category[]>('/catalog/categories');
  return data;
}

export function useCategories() {
  return useQuery({
    queryKey: ['categories'],
    queryFn: listCategories,
    staleTime: 5 * 60 * 1000,
  });
}

// The BFF fan-out (product + coarsened availability, never an exact
// count - the storefront never forwards a token to this route) rather
// than /api/catalog/products/by-sku, so a product page shows stock
// status without a second round trip.
async function getProductSummary(sku: string): Promise<ProductSummary> {
  const { data } = await apiClient.get<ProductSummary>(`/storefront/products/${encodeURIComponent(sku)}`);
  return data;
}

export function useProductSummary(sku: string | undefined) {
  return useQuery({
    queryKey: ['product-summary', sku],
    queryFn: () => getProductSummary(sku!),
    enabled: Boolean(sku),
  });
}
