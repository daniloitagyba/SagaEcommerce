"use strict";

const CART_ID_STORAGE_KEY = "storefront.cartId";
const CUSTOMER_ID_STORAGE_KEY = "storefront.customerId";

function getOrCreateId(storageKey, prefix) {
  let value = localStorage.getItem(storageKey);
  if (!value) {
    value = `${prefix}-${crypto.randomUUID()}`;
    localStorage.setItem(storageKey, value);
  }
  return value;
}

const cartId = getOrCreateId(CART_ID_STORAGE_KEY, "cart");
const customerId = getOrCreateId(CUSTOMER_ID_STORAGE_KEY, "customer");

async function fetchJson(url, options) {
  const response = await fetch(url, options);
  if (response.status === 204) {
    return null;
  }
  const body = await response.json().catch(() => null);
  if (!response.ok) {
    const message = body && body.message ? body.message : `Request to ${url} failed with ${response.status}`;
    throw new Error(message);
  }
  return body;
}

function formatCurrency(amount, currency) {
  return new Intl.NumberFormat("pt-BR", { style: "currency", currency: currency || "BRL" }).format(amount);
}

function productImage(product) {
  return (product.images && product.images[0]) || "https://picsum.photos/seed/placeholder/400/400";
}

function productCard(product, unitsSold) {
  const card = document.createElement("div");
  card.className = "product-card";
  card.innerHTML = `
    <img src="${productImage(product)}" alt="${product.name}" loading="lazy" />
    <div class="product-card-body">
      <h3>${product.name}</h3>
      <div class="product-price">${formatCurrency(product.price, product.currency)}</div>
      ${unitsSold != null ? `<div class="product-units-sold">${unitsSold} vendido${unitsSold === 1 ? "" : "s"}</div>` : ""}
      <button class="add-to-cart" data-sku="${product.sku}">Adicionar ao carrinho</button>
    </div>
  `;
  card.querySelector(".add-to-cart").addEventListener("click", (event) => addToCart(product.sku, event.target));
  return card;
}

async function renderBestsellersGlobal() {
  const grid = document.getElementById("bestsellers-global-grid");
  try {
    const data = await fetchJson("/api/catalog/products/bestsellers?limit=8");
    grid.innerHTML = "";
    if (!data.items.length) {
      grid.innerHTML = '<p class="empty-note">Ainda não há vendas registradas - as primeiras compras aparecerão aqui.</p>';
      return;
    }
    for (const entry of data.items) {
      grid.appendChild(productCard(entry.product, entry.unitsSold));
    }
  } catch (error) {
    grid.innerHTML = `<p class="empty-note">Não foi possível carregar os mais vendidos (${error.message}).</p>`;
  }
}

async function renderCategorySections() {
  const container = document.getElementById("category-sections");
  container.innerHTML = "";

  let categories;
  try {
    categories = await fetchJson("/api/catalog/categories");
  } catch (error) {
    container.innerHTML = `<p class="empty-note">Não foi possível carregar as categorias (${error.message}).</p>`;
    return;
  }

  for (const category of categories) {
    const section = document.createElement("section");
    section.className = "section";
    section.innerHTML = `<h2>${category.name}</h2><div class="product-grid"></div>`;
    container.appendChild(section);
    const grid = section.querySelector(".product-grid");

    try {
      const bestsellers = await fetchJson(`/api/catalog/products/bestsellers?category=${encodeURIComponent(category.slug)}&limit=6`);
      if (bestsellers.items.length) {
        for (const entry of bestsellers.items) {
          grid.appendChild(productCard(entry.product, entry.unitsSold));
        }
        continue;
      }
    } catch {
      // Fall through to the plain listing below.
    }

    // No sales yet in this category - show the catalog listing instead of an empty section.
    try {
      const listing = await fetchJson(`/api/catalog/products?category=${encodeURIComponent(category.slug)}&limit=6`);
      for (const product of listing.items) {
        grid.appendChild(productCard(product, null));
      }
    } catch (error) {
      grid.innerHTML = `<p class="empty-note">Não foi possível carregar produtos (${error.message}).</p>`;
    }
  }
}

async function getCart() {
  return fetchJson(`/api/cart/carts/${cartId}`);
}

async function addToCart(sku, buttonElement) {
  if (buttonElement) {
    buttonElement.disabled = true;
  }
  try {
    const cart = await getCart();
    const existing = cart.items.find((item) => item.sku === sku);
    const nextQuantity = (existing ? existing.quantity : 0) + 1;

    await fetchJson(`/api/cart/carts/${cartId}/items/${encodeURIComponent(sku)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ quantity: nextQuantity })
    });

    await renderCart();
  } catch (error) {
    alert(`Não foi possível adicionar ao carrinho: ${error.message}`);
  } finally {
    if (buttonElement) {
      buttonElement.disabled = false;
    }
  }
}

async function updateQuantity(sku, quantity) {
  if (quantity <= 0) {
    await removeFromCart(sku);
    return;
  }
  await fetchJson(`/api/cart/carts/${cartId}/items/${encodeURIComponent(sku)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ quantity })
  });
  await renderCart();
}

async function removeFromCart(sku) {
  await fetchJson(`/api/cart/carts/${cartId}/items/${encodeURIComponent(sku)}`, { method: "DELETE" });
  await renderCart();
}

function cartItemRow(item) {
  const row = document.createElement("div");
  row.className = "cart-item";
  row.innerHTML = `
    <div>
      <div class="cart-item-name">${item.productName}</div>
      <div class="product-price">${formatCurrency(item.unitPrice * item.quantity, item.currency)}</div>
    </div>
    <div class="cart-item-controls">
      <button data-action="decrement">−</button>
      <span>${item.quantity}</span>
      <button data-action="increment">+</button>
      <button class="cart-item-remove" data-action="remove">remover</button>
    </div>
  `;
  row.querySelector('[data-action="decrement"]').addEventListener("click", () => updateQuantity(item.sku, item.quantity - 1));
  row.querySelector('[data-action="increment"]').addEventListener("click", () => updateQuantity(item.sku, item.quantity + 1));
  row.querySelector('[data-action="remove"]').addEventListener("click", () => removeFromCart(item.sku));
  return row;
}

async function renderCart() {
  const itemsContainer = document.getElementById("cart-items");
  const totalElement = document.getElementById("cart-total");
  const countElement = document.getElementById("cart-count");
  const checkoutButton = document.getElementById("checkout-button");

  let cart;
  try {
    cart = await getCart();
  } catch {
    cart = { items: [], total: 0, currency: "BRL" };
  }

  itemsContainer.innerHTML = "";
  if (!cart.items.length) {
    itemsContainer.innerHTML = '<p class="empty-note">Seu carrinho está vazio.</p>';
  } else {
    for (const item of cart.items) {
      itemsContainer.appendChild(cartItemRow(item));
    }
  }

  totalElement.textContent = formatCurrency(cart.total, cart.currency);
  countElement.textContent = String(cart.items.reduce((sum, item) => sum + item.quantity, 0));
  checkoutButton.disabled = cart.items.length === 0;

  return cart;
}

async function checkout() {
  const checkoutButton = document.getElementById("checkout-button");
  const message = document.getElementById("checkout-message");
  const couponInput = document.getElementById("coupon-code");
  message.hidden = true;
  message.className = "checkout-message";

  checkoutButton.disabled = true;
  try {
    // The Storefront BFF (not this code) reads the cart and prices it
    // against the live catalog - see StorefrontEndpoints.CheckoutAsync.
    // Sending the cart's own total here would be exactly the "trust the
    // client's price" mistake Milestone 66 set out to remove; this call
    // carries no price at all, only which cart to check out.
    const couponCode = couponInput.value.trim() || undefined;
    const order = await fetchJson("/api/storefront/checkout", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ cartId, customerId, couponCode })
    });

    await renderCart();
    couponInput.value = "";

    message.textContent = describeOrder(order);
    message.className = "checkout-message success";
    message.hidden = false;
  } catch (error) {
    message.textContent = `Falha ao finalizar a compra: ${error.message}`;
    message.className = "checkout-message error";
    message.hidden = false;
  } finally {
    checkoutButton.disabled = false;
  }
}

function describeOrder(order) {
  const base = `Pedido ${order.id} criado! Ele passa agora pela saga de checkout (reserva de estoque, decisão de pagamento e confirmação ou compensação).`;
  if (!order.pricing || !order.pricing.discountTotal) {
    return base;
  }
  const discount = formatCurrency(order.pricing.discountTotal, order.currency);
  return `${base} Desconto aplicado: ${discount} sobre um subtotal de ${formatCurrency(order.pricing.subtotal, order.currency)}.`;
}

function setupCartPanel() {
  const panel = document.getElementById("cart-panel");
  const overlay = document.getElementById("cart-overlay");

  const open = () => {
    panel.hidden = false;
    overlay.hidden = false;
    renderCart();
  };
  const close = () => {
    panel.hidden = true;
    overlay.hidden = true;
  };

  document.getElementById("cart-toggle").addEventListener("click", open);
  document.getElementById("cart-close").addEventListener("click", close);
  overlay.addEventListener("click", close);
  document.getElementById("checkout-button").addEventListener("click", checkout);
}

async function init() {
  setupCartPanel();
  await Promise.all([renderBestsellersGlobal(), renderCategorySections(), renderCart()]);
}

init();
