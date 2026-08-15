import axios, { AxiosError } from 'axios';
import type { AxiosResponse } from 'axios';
import { getAccessToken, setAccessToken, triggerSigninRedirect } from '../auth/tokenStore';
import type { PriceMismatchProblem, ProblemDetails } from './types';

// Base "/api" - same-origin in production (Storefront.Service serves this
// app's own build and its API from one origin), proxied by Vite in dev
// (vite.config.ts) to whatever VITE_BFF_URL points at. Never a full URL
// here, on purpose - see Storefront.Service's own comment on why it has
// no CORS middleware at all.
export const apiClient = axios.create({
  baseURL: '/api',
});

apiClient.interceptors.request.use((config) => {
  const token = getAccessToken();
  if (token) {
    config.headers.Authorization = `Bearer ${token}`;
  }
  return config;
});

// A 401 while we believed we had a token means the session expired mid-use
// (not an anonymous shopper hitting a protected route - RequireAuth already
// redirects those before the request fires). Re-prompt login instead of
// leaving the shopper looking at a generic "Something went wrong".
apiClient.interceptors.response.use(
  (response) => response,
  (error: AxiosError) => {
    if (error.response?.status === 401 && getAccessToken()) {
      setAccessToken(null);
      triggerSigninRedirect();
    }
    return Promise.reject(error);
  },
);

/**
 * True for the one 409 shape this app treats as a distinct, recoverable
 * outcome rather than a generic failure. The `response` field is included
 * in the predicate type (not just the error) so a caller can read
 * `error.response.data` straight off the narrowed type - no non-null
 * assertion propped up by this function's own runtime check a few lines away.
 */
export function isPriceMismatch(
  error: unknown,
): error is AxiosError<PriceMismatchProblem> & { response: AxiosResponse<PriceMismatchProblem> } {
  return (
    axios.isAxiosError<PriceMismatchProblem>(error) &&
    error.response?.status === 409 &&
    error.response.data?.title === 'Price Changed'
  );
}

/** Pulls a human-readable message out of whichever ProblemDetails shape the backend sent - a validation problem's `errors` map, or a plain `detail`. */
export function describeApiError(error: unknown): string {
  if (axios.isAxiosError<ProblemDetails>(error) && error.response) {
    const problem = error.response.data;
    if (problem?.errors) {
      return Object.values(problem.errors).flat().join(' ');
    }
    if (problem?.detail) {
      return problem.detail;
    }
    if (problem?.title) {
      return problem.title;
    }
  }
  return 'Something went wrong. Please try again.';
}
