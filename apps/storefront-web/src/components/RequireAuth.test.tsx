import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen } from '@testing-library/react';
import { useAuth } from 'react-oidc-context';
import { RequireAuth } from './RequireAuth';

vi.mock('react-oidc-context', () => ({
  useAuth: vi.fn(),
}));

const mockedUseAuth = vi.mocked(useAuth);

describe('RequireAuth', () => {
  beforeEach(() => {
    mockedUseAuth.mockReset();
  });

  it('redirects to sign-in and shows a spinner when unauthenticated', () => {
    const signinRedirect = vi.fn();
    mockedUseAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: false,
      activeNavigator: undefined,
      signinRedirect,
    } as never);

    render(
      <RequireAuth>
        <div>protected content</div>
      </RequireAuth>,
    );

    expect(signinRedirect).toHaveBeenCalledOnce();
    expect(screen.queryByText('protected content')).not.toBeInTheDocument();
  });

  it('renders children once authenticated', () => {
    mockedUseAuth.mockReturnValue({
      isLoading: false,
      isAuthenticated: true,
      activeNavigator: undefined,
      signinRedirect: vi.fn(),
    } as never);

    render(
      <RequireAuth>
        <div>protected content</div>
      </RequireAuth>,
    );

    expect(screen.getByText('protected content')).toBeInTheDocument();
  });

  it('does not redirect while still loading or mid-navigation', () => {
    const signinRedirect = vi.fn();
    mockedUseAuth.mockReturnValue({
      isLoading: true,
      isAuthenticated: false,
      activeNavigator: undefined,
      signinRedirect,
    } as never);

    render(
      <RequireAuth>
        <div>protected content</div>
      </RequireAuth>,
    );

    expect(signinRedirect).not.toHaveBeenCalled();
  });
});
