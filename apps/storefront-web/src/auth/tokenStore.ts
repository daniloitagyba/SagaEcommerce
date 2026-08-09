// A plain module-level holder for the current access token, kept outside
// React state on purpose: the axios interceptor (api/client.ts) and any
// plain-function API call need to read it synchronously, outside a
// component's render, which a hook alone can't give them. AppRoot updates
// this whenever react-oidc-context's user changes; nothing else writes to it.
let currentAccessToken: string | null = null;

export function setAccessToken(token: string | null): void {
  currentAccessToken = token;
}

export function getAccessToken(): string | null {
  return currentAccessToken;
}
