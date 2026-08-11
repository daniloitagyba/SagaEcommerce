import { describe, expect, it, vi, afterEach } from 'vitest';
import {
  getAccessToken,
  setAccessToken,
  setSigninRedirectCallback,
  triggerSigninRedirect,
} from './tokenStore';

// This module holds mutable module-level state (see its own comment on
// why) - reset it after every test so one test's token/callback can't
// leak into the next.
afterEach(() => {
  setAccessToken(null);
  setSigninRedirectCallback(null);
});

describe('tokenStore', () => {
  it('has no token until one is set', () => {
    expect(getAccessToken()).toBeNull();
  });

  it('returns whatever token was last set', () => {
    setAccessToken('token-1');
    expect(getAccessToken()).toBe('token-1');

    setAccessToken('token-2');
    expect(getAccessToken()).toBe('token-2');
  });

  it('clears the token when set to null', () => {
    setAccessToken('token-1');
    setAccessToken(null);
    expect(getAccessToken()).toBeNull();
  });

  it('invokes the registered callback when a redirect is triggered', () => {
    const callback = vi.fn();
    setSigninRedirectCallback(callback);

    triggerSigninRedirect();

    expect(callback).toHaveBeenCalledOnce();
  });

  it('does nothing when no callback is registered', () => {
    expect(() => triggerSigninRedirect()).not.toThrow();
  });

  it('stops invoking a callback once it is cleared', () => {
    const callback = vi.fn();
    setSigninRedirectCallback(callback);
    setSigninRedirectCallback(null);

    triggerSigninRedirect();

    expect(callback).not.toHaveBeenCalled();
  });
});
