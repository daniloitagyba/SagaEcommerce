import http from 'k6/http';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { setTimeout } from 'k6/timers';

const profileName = __ENV.PROFILE || 'smoke';
const baseUrl = (__ENV.BASE_URL || '').replace(/\/$/, '');
const runId = __ENV.RUN_ID || `${Date.now()}`;
const accessToken = __ENV.ACCESS_TOKEN || '';
const podIps = (__ENV.ORDERS_API_POD_IPS || '').split(',').map((ip) => ip.trim()).filter(Boolean);
const hedgeDelayMs = Number(__ENV.HEDGE_DELAY_MS || '20');

if (!baseUrl && profileName !== 'hedged') {
  throw new Error('BASE_URL is required.');
}

if (!accessToken) {
  throw new Error('ACCESS_TOKEN is required (see scripts/keycloak-get-token.sh).');
}

const authHeaders = { Authorization: `Bearer ${accessToken}` };

const commonThresholds = {
  checks: ['rate>0.99'],
  http_req_failed: ['rate<0.01'],
  order_flow_success: ['rate>0.99'],
  'http_req_duration{endpoint:create-order}': ['p(95)<500', 'p(99)<1000'],
  'http_req_duration{endpoint:get-order}': ['p(95)<300', 'p(99)<750'],
};

const profiles = {
  smoke: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 1,
        duration: '10s',
        gracefulStop: '10s',
      },
    },
    thresholds: {
      checks: ['rate==1'],
      http_req_failed: ['rate==0'],
      order_flow_success: ['rate==1'],
      'http_req_duration{endpoint:create-order}': ['p(95)<750'],
      'http_req_duration{endpoint:get-order}': ['p(95)<500'],
    },
  },
  baseline: {
    scenarios: {
      orders: {
        executor: 'ramping-vus',
        startVUs: 0,
        stages: [
          { duration: '10s', target: 5 },
          { duration: '20s', target: 5 },
          { duration: '10s', target: 10 },
          { duration: '20s', target: 10 },
          { duration: '10s', target: 0 },
        ],
        gracefulRampDown: '10s',
        gracefulStop: '15s',
      },
    },
    thresholds: commonThresholds,
  },
  autoscale: {
    scenarios: {
      orders: {
        executor: 'ramping-vus',
        startVUs: 0,
        stages: [
          { duration: '15s', target: 75 },
          { duration: '60s', target: 75 },
          { duration: '15s', target: 0 },
        ],
        gracefulRampDown: '15s',
        gracefulStop: '20s',
      },
    },
    thresholds: {
      ...commonThresholds,
      'http_req_duration{endpoint:create-order}': ['p(95)<750', 'p(99)<1500'],
      'http_req_duration{endpoint:get-order}': ['p(95)<500', 'p(99)<1000'],
    },
  },
  resilience: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 5,
        duration: '75s',
        gracefulStop: '20s',
      },
    },
    thresholds: {
      checks: ['rate==1'],
      http_req_failed: ['rate==0'],
      order_flow_success: ['rate==1'],
      'http_req_duration{endpoint:create-order}': ['p(95)<750', 'p(99)<1500'],
      'http_req_duration{endpoint:get-order}': ['p(95)<500', 'p(99)<1000'],
    },
  },
  stress: {
    scenarios: {
      orders: {
        executor: 'ramping-vus',
        startVUs: 0,
        stages: [
          { duration: '15s', target: 10 },
          { duration: '30s', target: 20 },
          { duration: '30s', target: 30 },
          { duration: '15s', target: 0 },
        ],
        gracefulRampDown: '15s',
        gracefulStop: '20s',
      },
    },
    thresholds: {
      ...commonThresholds,
      checks: ['rate>0.98'],
      http_req_failed: ['rate<0.02'],
      order_flow_success: ['rate>0.98'],
      'http_req_duration{endpoint:create-order}': ['p(95)<750', 'p(99)<1500'],
      'http_req_duration{endpoint:get-order}': ['p(95)<500', 'p(99)<1000'],
    },
  },
  soak: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 5,
        duration: '5m',
        gracefulStop: '20s',
      },
    },
    thresholds: commonThresholds,
  },
  cache: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 10,
        duration: '30s',
        gracefulStop: '10s',
      },
    },
    thresholds: {
      checks: ['rate>0.99'],
      http_req_failed: ['rate<0.01'],
      cache_hit_rate: ['rate>0.90'],
      'http_req_duration{endpoint:get-order-cached}': ['p(95)<100', 'p(99)<250'],
    },
  },
  chaos: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 5,
        duration: '40s',
        gracefulStop: '15s',
      },
    },
    thresholds: {
      checks: ['rate>0.95'],
      http_req_failed: ['rate<0.05'],
      order_flow_success: ['rate>0.95'],
      'http_req_duration{endpoint:create-order}': ['p(95)<3000'],
      'http_req_duration{endpoint:get-order}': ['p(95)<3000'],
    },
  },
  overload: {
    scenarios: {
      orders: {
        executor: 'ramping-vus',
        startVUs: 0,
        stages: [
          { duration: '10s', target: 300 },
          { duration: '20s', target: 300 },
          { duration: '10s', target: 0 },
        ],
        gracefulRampDown: '10s',
        gracefulStop: '15s',
      },
    },
    thresholds: {
      server_error_rate: ['rate<0.01'],
      rate_limited_rate: ['rate>0.05'],
      accepted_duration: ['p(95)<1500'],
    },
  },
  saga: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 10,
        duration: '30s',
        gracefulStop: '25s',
      },
    },
    thresholds: {
      checks: ['rate>0.99'],
      http_req_failed: ['rate<0.01'],
      saga_converged_rate: ['rate>0.99'],
      saga_correct_outcome_rate: ['rate==1'],
    },
  },
  hedged: {
    scenarios: {
      orders: {
        executor: 'constant-vus',
        vus: 2,
        duration: '45s',
        gracefulStop: '10s',
      },
    },
    thresholds: {
      checks: ['rate>0.99'],
      http_req_failed: ['rate<0.01'],
    },
  },
};

if (!profiles[profileName]) {
  throw new Error(
    `Unsupported PROFILE "${profileName}". Use smoke, baseline, autoscale, resilience, stress, soak, cache, chaos, overload, saga, or hedged.`,
  );
}

if (profileName === 'hedged' && podIps.length < 2) {
  throw new Error('PROFILE=hedged requires ORDERS_API_POD_IPS with at least 2 comma-separated pod IPs.');
}

export const options = {
  ...profiles[profileName],
  discardResponseBodies: false,
  summaryTrendStats: ['avg', 'min', 'med', 'max', 'p(90)', 'p(95)', 'p(99)'],
  tags: {
    workload: 'orders',
    profile: profileName,
  },
};

const createdOrders = new Counter('orders_created');
const instanceRequests = new Counter('api_instance_requests');
const successfulFlows = new Rate('order_flow_success');
const cacheHitRate = new Rate('cache_hit_rate');
const rateLimitedRate = new Rate('rate_limited_rate');
const serverErrorRate = new Rate('server_error_rate');
const acceptedDuration = new Trend('accepted_duration');
const sagaConvergedRate = new Rate('saga_converged_rate');
const sagaCorrectOutcomeRate = new Rate('saga_correct_outcome_rate');
const sagaConvergenceDuration = new Trend('saga_convergence_duration_ms');
const unhedgedReadDuration = new Trend('hedged_unhedged_read_duration_ms');
const hedgedReadDuration = new Trend('hedged_hedged_read_duration_ms');
const hedgeFiredRate = new Rate('hedged_hedge_fired_rate');
const hedgeWonRate = new Rate('hedged_hedge_won_rate');

const SAGA_DECLINE_THRESHOLD = 1000;
const SAGA_POLL_ATTEMPTS = 40;
const SAGA_POLL_DELAY_SECONDS = 0.5;

const CACHE_SEED_POOL_SIZE = 15;

export function setup() {
  if (profileName !== 'cache') {
    return {};
  }

  const orderIds = [];
  for (let index = 0; index < CACHE_SEED_POOL_SIZE; index += 1) {
    const payload = JSON.stringify({
      customerId: `cache-seed-customer-${index}`,
      items: [{ sku: 'SKU-BOOK-002', quantity: 1 }],
    });
    const response = http.post(`${baseUrl}/orders`, payload, {
      headers: {
        'Content-Type': 'application/json',
        'X-Correlation-ID': `k6-cache-seed-${runId}-${index}`,
        ...authHeaders,
      },
      timeout: '10s',
    });
    if (response.status === 201) {
      const id = response.json('id');
      if (id) {
        orderIds.push(id);
      }
    }
  }

  sleep(2);
  return { orderIds };
}

export default async function (data) {
  if (profileName === 'cache') {
    cacheWorkload(data);
    return;
  }

  if (profileName === 'overload') {
    overloadWorkload();
    return;
  }

  if (profileName === 'saga') {
    sagaWorkload();
    return;
  }

  if (profileName === 'hedged') {
    await hedgedWorkload();
    return;
  }

  ordersWorkload();
}

function sagaWorkload() {
  const iterationId = `${runId}-${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
  const shouldDecline = exec.scenario.iterationInTest % 2 === 1;
  const expectedStatus = shouldDecline ? 'Cancelled' : 'Confirmed';

  const payload = JSON.stringify({
    customerId: `saga-customer-${iterationId}`,
    items: shouldDecline
      ? [{ sku: 'SKU-ELEC-001', quantity: 1 }]
      : [{ sku: 'SKU-BOOK-002', quantity: 1 }],
  });

  const createResponse = http.post(`${baseUrl}/orders`, payload, {
    headers: {
      'Content-Type': 'application/json',
      'X-Correlation-ID': `k6-saga-${iterationId}`,
      ...authHeaders,
    },
    tags: {
      endpoint: 'create-order',
      name: 'POST /orders',
    },
    timeout: '10s',
  });

  const created = check(createResponse, {
    'saga order creation returns 201': (res) => res.status === 201,
  });
  if (!created) {
    sagaConvergedRate.add(false);
    sagaCorrectOutcomeRate.add(false);
    sleep(1);
    return;
  }

  const orderId = createResponse.json('id');
  const startedAt = Date.now();

  for (let attempt = 0; attempt < SAGA_POLL_ATTEMPTS; attempt += 1) {
    sleep(SAGA_POLL_DELAY_SECONDS);

    const getResponse = http.get(`${baseUrl}/orders/${orderId}`, {
      headers: authHeaders,
      tags: {
        endpoint: 'get-order',
        name: 'GET /orders/:id',
      },
      timeout: '10s',
    });

    if (getResponse.status !== 200) {
      continue;
    }

    const status = getResponse.json('status');
    if (status === 'Confirmed' || status === 'Cancelled') {
      sagaConvergenceDuration.add(Date.now() - startedAt);
      sagaConvergedRate.add(true);
      sagaCorrectOutcomeRate.add(status === expectedStatus);
      return;
    }
  }

  sagaConvergedRate.add(false);
  sagaCorrectOutcomeRate.add(false);
}

function pickTwoDistinctPods() {
  const first = podIps[Math.floor(Math.random() * podIps.length)];
  let second = first;
  while (second === first && podIps.length > 1) {
    second = podIps[Math.floor(Math.random() * podIps.length)];
  }
  return [first, second];
}

function createOrderDirect(pod, label, iterationId) {
  const payload = JSON.stringify({
    customerId: `hedged-${label}-${iterationId}`,
    items: [{ sku: 'SKU-BOOK-002', quantity: 1 }],
  });
  const response = http.post(`http://${pod}:8080/orders`, payload, {
    headers: {
      'Content-Type': 'application/json',
      'X-Correlation-ID': `k6-hedged-${label}-${iterationId}`,
      ...authHeaders,
    },
    tags: { endpoint: 'create-order', name: 'POST /orders' },
    timeout: '10s',
  });
  return response.status === 201 ? response.json('id') : null;
}

async function hedgedGet(orderId, tags) {
  const [podA, podB] = pickTwoDistinctPods();
  const primary = http
    .asyncRequest('GET', `http://${podA}:8080/orders/${orderId}`, null, {
      headers: authHeaders,
      tags,
      timeout: '10s',
    })
    .then((response) => ({ response, hedged: false }));

  const hedgeTimeout = new Promise((resolve) => {
    setTimeout(() => resolve({ timedOut: true }), hedgeDelayMs);
  });

  const firstResult = await Promise.race([primary, hedgeTimeout]);
  if (!firstResult.timedOut) {
    return { winner: firstResult, hedgeFired: false };
  }

  const hedge = http
    .asyncRequest('GET', `http://${podB}:8080/orders/${orderId}`, null, {
      headers: authHeaders,
      tags,
      timeout: '10s',
    })
    .then((response) => ({ response, hedged: true }));

  const winner = await Promise.race([primary, hedge]);
  return { winner, hedgeFired: true };
}

async function hedgedWorkload() {
  const iterationId = `${runId}-${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
  const anyPod = podIps[Math.floor(Math.random() * podIps.length)];

  const unhedgedOrderId = createOrderDirect(anyPod, 'unhedged', iterationId);
  const hedgedOrderId = createOrderDirect(anyPod, 'hedged', iterationId);

  if (!unhedgedOrderId || !hedgedOrderId) {
    successfulFlows.add(false);
    sleep(1);
    return;
  }

  const unhedgedPod = podIps[Math.floor(Math.random() * podIps.length)];
  const unhedgedTags = { endpoint: 'get-order-unhedged', name: 'GET /orders/:id (unhedged)' };
  const unhedgedStart = Date.now();
  const unhedgedResponse = http.get(`http://${unhedgedPod}:8080/orders/${unhedgedOrderId}`, {
    headers: authHeaders,
    tags: unhedgedTags,
    timeout: '10s',
  });
  unhedgedReadDuration.add(Date.now() - unhedgedStart);
  const unhedgedOk = check(unhedgedResponse, { 'unhedged read returns 200': (res) => res.status === 200 });

  const hedgedTags = { endpoint: 'get-order-hedged', name: 'GET /orders/:id (hedged)' };
  const hedgedStart = Date.now();
  const { winner, hedgeFired } = await hedgedGet(hedgedOrderId, hedgedTags);
  hedgedReadDuration.add(Date.now() - hedgedStart);
  hedgeFiredRate.add(hedgeFired);
  if (hedgeFired) {
    hedgeWonRate.add(winner.hedged);
  }
  const hedgedOk = check(winner.response, { 'hedged read returns 200': (res) => res.status === 200 });

  successfulFlows.add(unhedgedOk && hedgedOk);
  sleep(0.6 + Math.random() * 0.3);
}

function overloadWorkload() {
  const iterationId = `${runId}-${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
  const payload = JSON.stringify({
    customerId: `overload-customer-${iterationId}`,
    items: [{ sku: 'SKU-BOOK-002', quantity: 1 }],
  });

  const response = http.post(`${baseUrl}/orders`, payload, {
    headers: {
      'Content-Type': 'application/json',
      'X-Correlation-ID': `k6-overload-${iterationId}`,
      ...authHeaders,
    },
    tags: {
      endpoint: 'create-order',
      name: 'POST /orders',
    },
    timeout: '10s',
  });

  const accepted = response.status === 201;
  const rateLimited = response.status === 429;
  const serverError = response.status >= 500;

  rateLimitedRate.add(rateLimited);
  serverErrorRate.add(serverError);

  if (rateLimited) {
    check(response, {
      'rate limited response has Retry-After header': (res) => !!res.headers['Retry-After'],
    });
  }

  if (accepted) {
    acceptedDuration.add(response.timings.duration);
  }

  sleep(0.2 + Math.random() * 0.1);
}

function cacheWorkload(data) {
  const orderIds = (data && data.orderIds) || [];
  if (orderIds.length === 0) {
    sleep(1);
    return;
  }

  const orderId = orderIds[Math.floor(Math.random() * orderIds.length)];
  const response = http.get(`${baseUrl}/orders/${orderId}`, {
    headers: authHeaders,
    tags: {
      endpoint: 'get-order-cached',
      name: 'GET /orders/:id (cached)',
    },
    timeout: '10s',
  });

  const succeeded = check(response, {
    'cached order read returns 200': (res) => res.status === 200,
  });

  cacheHitRate.add(response.headers['X-Cache'] === 'HIT');
  successfulFlows.add(succeeded);
  sleep(0.2 + Math.random() * 0.2);
}

function ordersWorkload() {
  const iterationId = `${runId}-${exec.vu.idInTest}-${exec.scenario.iterationInTest}`;
  const correlationId = `k6-${iterationId}`;
  const payload = JSON.stringify({
    customerId: `performance-customer-${iterationId}`,
    items: [{ sku: 'SKU-BOOK-002', quantity: 1 }],
  });

  const createResponse = http.post(`${baseUrl}/orders`, payload, {
    headers: {
      'Content-Type': 'application/json',
      'X-Correlation-ID': correlationId,
      ...authHeaders,
    },
    tags: {
      endpoint: 'create-order',
      name: 'POST /orders',
    },
    timeout: '10s',
  });

  const responseCorrelationId = createResponse.headers['X-Correlation-Id'];
  const instanceId = createResponse.headers['X-Instance-Id'] || 'missing';
  let orderId = '';

  if (createResponse.status === 201) {
    try {
      orderId = createResponse.json('id') || '';
    } catch {
      orderId = '';
    }
  }

  const createSucceeded = check(createResponse, {
    'order creation returns 201': (response) => response.status === 201,
    'order creation returns an identifier': () => orderId.length > 0,
    'correlation header is preserved': () => responseCorrelationId === correlationId,
    'instance header identifies a pod': () => instanceId !== 'missing',
  });

  if (!createSucceeded) {
    successfulFlows.add(false);
    sleep(1);
    return;
  }

  createdOrders.add(1);
  instanceRequests.add(1, { instance_id: instanceId });

  const getResponse = http.get(`${baseUrl}/orders/${orderId}`, {
    headers: authHeaders,
    tags: {
      endpoint: 'get-order',
      name: 'GET /orders/:id',
    },
    timeout: '10s',
  });

  const readSucceeded = check(getResponse, {
    'created order can be read': (response) => response.status === 200,
    'read order identifier matches': (response) => response.json('id') === orderId,
  });

  successfulFlows.add(readSucceeded);
  sleep(0.8 + Math.random() * 0.4);
}
