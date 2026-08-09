import { useState } from 'react';
import { Link as RouterLink, useSearchParams } from 'react-router-dom';
import Grid from '@mui/material/Grid';
import Card from '@mui/material/Card';
import CardActionArea from '@mui/material/CardActionArea';
import CardContent from '@mui/material/CardContent';
import CardMedia from '@mui/material/CardMedia';
import Typography from '@mui/material/Typography';
import Chip from '@mui/material/Chip';
import Stack from '@mui/material/Stack';
import ToggleButton from '@mui/material/ToggleButton';
import ToggleButtonGroup from '@mui/material/ToggleButtonGroup';
import Pagination from '@mui/material/Pagination';
import Alert from '@mui/material/Alert';
import Skeleton from '@mui/material/Skeleton';
import { useCategories, useProducts } from '../api/catalog';
import { formatMoney } from '../format';

const PAGE_SIZE = 12;

export function CatalogPage() {
  const [searchParams, setSearchParams] = useSearchParams();
  const category = searchParams.get('category') ?? undefined;
  const page = Number(searchParams.get('page') ?? '1');

  const { data: categories } = useCategories();
  const { data, isLoading, isError } = useProducts({
    category,
    skip: (page - 1) * PAGE_SIZE,
    limit: PAGE_SIZE,
  });

  const [imageErrors, setImageErrors] = useState<Set<string>>(new Set());

  const setCategory = (value: string | null) => {
    const next = new URLSearchParams(searchParams);
    if (value) {
      next.set('category', value);
    } else {
      next.delete('category');
    }
    next.delete('page');
    setSearchParams(next);
  };

  const setPage = (value: number) => {
    const next = new URLSearchParams(searchParams);
    next.set('page', String(value));
    setSearchParams(next);
  };

  const pageCount = data ? Math.max(1, Math.ceil(data.total / PAGE_SIZE)) : 1;

  return (
    <Stack spacing={3}>
      <Typography variant="h4" component="h1">
        Products
      </Typography>

      {categories && categories.length > 0 && (
        <ToggleButtonGroup
          value={category ?? 'all'}
          exclusive
          size="small"
          onChange={(_event, value) => setCategory(value === 'all' ? null : value)}
          sx={{ flexWrap: 'wrap' }}
        >
          <ToggleButton value="all">All</ToggleButton>
          {categories.map((c) => (
            <ToggleButton key={c.slug} value={c.slug}>
              {c.name}
            </ToggleButton>
          ))}
        </ToggleButtonGroup>
      )}

      {isError && <Alert severity="error">Could not load products. The catalog service may be unavailable.</Alert>}

      <Grid container spacing={3}>
        {isLoading &&
          Array.from({ length: PAGE_SIZE }).map((_, index) => (
            // eslint-disable-next-line react/no-array-index-key
            <Grid key={index} size={{ xs: 12, sm: 6, md: 4, lg: 3 }}>
              <Skeleton variant="rectangular" height={160} />
              <Skeleton variant="text" />
              <Skeleton variant="text" width="60%" />
            </Grid>
          ))}

        {data?.items.map((product) => (
          <Grid key={product.id} size={{ xs: 12, sm: 6, md: 4, lg: 3 }}>
            <Card variant="outlined" className="h-full">
              <CardActionArea component={RouterLink} to={`/products/${product.sku}`} className="h-full">
                {product.images[0] && !imageErrors.has(product.sku) ? (
                  <CardMedia
                    component="img"
                    height={160}
                    image={product.images[0]}
                    alt={product.name}
                    onError={() => setImageErrors((prev) => new Set(prev).add(product.sku))}
                    sx={{ objectFit: 'cover' }}
                  />
                ) : (
                  <div className="flex h-40 items-center justify-center bg-gray-100 text-gray-400">
                    No image
                  </div>
                )}
                <CardContent>
                  <Chip label={product.categorySlug} size="small" sx={{ mb: 1 }} />
                  <Typography variant="subtitle1" component="h2" noWrap>
                    {product.name}
                  </Typography>
                  <Typography variant="body1" sx={{ fontWeight: 600 }}>
                    {formatMoney(product.price, product.currency)}
                  </Typography>
                </CardContent>
              </CardActionArea>
            </Card>
          </Grid>
        ))}
      </Grid>

      {!isLoading && data && data.items.length === 0 && (
        <Typography color="text.secondary">No products found in this category.</Typography>
      )}

      {pageCount > 1 && (
        <Stack sx={{ alignItems: 'center' }}>
          <Pagination count={pageCount} page={page} onChange={(_event, value) => setPage(value)} />
        </Stack>
      )}
    </Stack>
  );
}
