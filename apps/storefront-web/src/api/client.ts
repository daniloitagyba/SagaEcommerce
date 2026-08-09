import axios, { AxiosError } from 'axios';
import { getAccessToken } from '../auth/tokenStore';
import type { ProblemDetails } from './types';

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

/** True for the one 409 shape this app treats as a distinct, recoverable outcome rather than a generic failure. */
export function isPriceMismatch(
  error: unknown,
): error is AxiosError<ProblemDetails & { expectedSubtotal: number; actualSubtotal: number }> {
  return (
    axios.isAxiosError(error) &&
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
