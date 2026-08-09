// Wire types, kept in one place and named after what Storefront.Service
// and the services behind it actually return - see the API reference this
// was built against (Storefront.Service/{StorefrontEndpoints,ProxyEndpoints}.cs,
// Cart.Service/Endpoints/CartEndpoints.cs, Orders.Api/{Contracts,Endpoints}/*,
// Catalog.Service/Endpoints/*). Not generated - the backend has no OpenAPI
// document to generate from - so keeping this file the single place that
// changes when a contract does is deliberate.

export interface Product {
  id: string;
  name: string;
  description: string;
  categorySlug: string;
  price: number;
  currency: string;
  sku: string;
  attributes: Record<string, string>;
  images: string[];
  createdAt: string;
}

export interface ProductListResponse {
  items: Product[];
  total: number;
  skip: number;
  limit: number;
}

export interface Category {
  id: string;
  slug: string;
  name: string;
}

export type Availability = 'OutOfStock' | 'Low' | 'InStock';

// Anonymous shape only - the storefront never forwards a token to the
// product-summary fan-out, so this is the only inventory shape it ever sees.
export interface AnonymousInventory {
  sku: string;
  availability: Availability;
}

export interface ProductSummary {
  product: Product | null;
  inventory: AnonymousInventory | null;
  degraded: boolean;
}

export interface CartLineItem {
  sku: string;
  quantity: number;
  unitPrice: number;
  currency: string;
  productName: string;
  addedAt: string;
}

export interface Cart {
  items: CartLineItem[];
  total: number;
  currency: string;
  expiresInSeconds: number | null;
  version: number;
}

export interface ShippingAddress {
  line1?: string;
  city?: string;
  region?: string;
  postalCode?: string;
}

export type PaymentMethod = 'Card' | 'Pix' | 'Boleto';

export interface CheckoutRequest {
  couponCode?: string;
  paymentMethod?: PaymentMethod;
  shippingAddress?: ShippingAddress;
}

export interface OrderLine {
  sku: string;
  productName: string;
  quantity: number;
  unitPrice: number;
  lineSubtotal: number;
  lineDiscount: number;
  lineTotal: number;
}

export interface OrderPricing {
  subtotal: number;
  discountTotal: number;
  shippingTotal: number;
  taxTotal: number;
  couponCode: string | null;
  lines: OrderLine[];
}

export type OrderStatus =
  | 'Created'
  | 'Confirmed'
  | 'Backordered'
  | 'Picking'
  | 'Shipped'
  | 'Delivered'
  | 'Cancelled'
  | 'Returned'
  | 'FulfillmentHold';

export interface Order {
  id: string;
  customerId: string;
  amount: number;
  currency: string;
  status: OrderStatus;
  createdAt: string;
  correlationId: string;
  instanceId: string;
  pricing: OrderPricing | null;
  paymentMethod: string | null;
}

// The one shape of ProblemDetails this app actually branches on - a 409
// with expectedSubtotal/actualSubtotal means the price moved since the
// cart was last priced, not a generic failure.
export interface PriceMismatchProblem {
  detail: string;
  status: 409;
  title: 'Price Changed';
  expectedSubtotal: number;
  actualSubtotal: number;
}

export interface ProblemDetails {
  detail?: string;
  status?: number;
  title?: string;
  errors?: Record<string, string[]>;
  [key: string]: unknown;
}

export interface OrderSummary {
  orderId: string;
  customerId: string | null;
  amount: number | null;
  currency: string | null;
  status: OrderStatus;
  orderCreatedAt: string | null;
  decidedAt: string | null;
  projectedAt: string;
}

export interface OrderSummaryPage {
  items: OrderSummary[];
  nextCursor: string | null;
}

export interface OrderEvent {
  id: number;
  eventType: string;
  occurredAt: string;
}

export interface OrderHistory {
  orderId: string;
  snapshot: {
    customerId: string | null;
    amount: number | null;
    currency: string | null;
    status: OrderStatus;
    createdAt: string | null;
  } | null;
  events: OrderEvent[];
}

export interface CancellationResponse {
  orderId: string;
  status: OrderStatus;
  correlationId: string;
}

export type ReturnReasonCategory = 'Defect' | 'Regret' | 'Unwanted';

export interface CreateReturnItemRequest {
  sku: string;
  quantity: number;
}

export interface CreateReturnRequest {
  items: CreateReturnItemRequest[];
  reason?: string;
  reasonCategory?: ReturnReasonCategory;
}

export interface ReturnResponse {
  returnId: string;
  orderId: string;
  refundTotal: number;
  orderFullyReturned: boolean;
  correlationId: string;
}
