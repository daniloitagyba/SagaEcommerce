import { describe, expect, it, vi } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { useCategories, useProducts } from '../api/catalog';
import { CatalogPage } from './CatalogPage';

vi.mock('../api/catalog', () => ({
  useCategories: vi.fn(),
  useProducts: vi.fn(),
}));

const mockedUseCategories = vi.mocked(useCategories);
const mockedUseProducts = vi.mocked(useProducts);

describe('CatalogPage', () => {
  it('shows the empty state when the API returns no products', () => {
    mockedUseCategories.mockReturnValue({ data: [] } as never);
    mockedUseProducts.mockReturnValue({
      data: { items: [], total: 0, skip: 0, limit: 12 },
      isLoading: false,
      isError: false,
    } as never);

    render(
      <MemoryRouter>
        <CatalogPage />
      </MemoryRouter>,
    );

    expect(screen.getByText('No products found in this category.')).toBeInTheDocument();
  });

  it('shows an error alert when the products request fails', () => {
    mockedUseCategories.mockReturnValue({ data: [] } as never);
    mockedUseProducts.mockReturnValue({
      data: undefined,
      isLoading: false,
      isError: true,
    } as never);

    render(
      <MemoryRouter>
        <CatalogPage />
      </MemoryRouter>,
    );

    expect(
      screen.getByText('Could not load products. The catalog service may be unavailable.'),
    ).toBeInTheDocument();
  });
});
