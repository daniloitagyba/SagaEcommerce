import { lazy, Suspense } from 'react';
import { Route, Routes } from 'react-router-dom';
import Box from '@mui/material/Box';
import CircularProgress from '@mui/material/CircularProgress';
import { Layout } from './components/Layout';
import { CatalogPage } from './pages/CatalogPage';

// CatalogPage is the landing page - loaded eagerly, same as Layout. Every
// other route is a click away at the earliest, so splitting them out keeps
// the first paint from waiting on MUI/oidc-client-ts code the shopper may
// never need this visit (checkout, order history, ...).
const ProductDetailPage = lazy(() =>
  import('./pages/ProductDetailPage').then((m) => ({ default: m.ProductDetailPage })),
);
const CartPage = lazy(() => import('./pages/CartPage').then((m) => ({ default: m.CartPage })));
const CheckoutPage = lazy(() =>
  import('./pages/CheckoutPage').then((m) => ({ default: m.CheckoutPage })),
);
const OrdersPage = lazy(() =>
  import('./pages/OrdersPage').then((m) => ({ default: m.OrdersPage })),
);
const OrderDetailPage = lazy(() =>
  import('./pages/OrderDetailPage').then((m) => ({ default: m.OrderDetailPage })),
);
const NotFoundPage = lazy(() =>
  import('./pages/NotFoundPage').then((m) => ({ default: m.NotFoundPage })),
);

function RouteFallback() {
  return (
    <Box sx={{ display: 'flex', justifyContent: 'center', py: 8 }}>
      <CircularProgress />
    </Box>
  );
}

export function App() {
  return (
    <Routes>
      <Route element={<Layout />}>
        <Route path="/" element={<CatalogPage />} />
        <Route
          path="/products/:sku"
          element={
            <Suspense fallback={<RouteFallback />}>
              <ProductDetailPage />
            </Suspense>
          }
        />
        <Route
          path="/cart"
          element={
            <Suspense fallback={<RouteFallback />}>
              <CartPage />
            </Suspense>
          }
        />
        <Route
          path="/checkout"
          element={
            <Suspense fallback={<RouteFallback />}>
              <CheckoutPage />
            </Suspense>
          }
        />
        <Route
          path="/orders"
          element={
            <Suspense fallback={<RouteFallback />}>
              <OrdersPage />
            </Suspense>
          }
        />
        <Route
          path="/orders/:id"
          element={
            <Suspense fallback={<RouteFallback />}>
              <OrderDetailPage />
            </Suspense>
          }
        />
        <Route
          path="*"
          element={
            <Suspense fallback={<RouteFallback />}>
              <NotFoundPage />
            </Suspense>
          }
        />
      </Route>
    </Routes>
  );
}
