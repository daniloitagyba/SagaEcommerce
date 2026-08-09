import { Component } from 'react';
import type { ErrorInfo, ReactNode } from 'react';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Button from '@mui/material/Button';
import Container from '@mui/material/Container';

interface ErrorBoundaryProps {
  children: ReactNode;
}

interface ErrorBoundaryState {
  hasError: boolean;
}

/** Class component on purpose - componentDidCatch has no hook equivalent. Catches a render/lifecycle throw anywhere below it and shows a recovery UI instead of an unrecoverable white screen. */
export class ErrorBoundary extends Component<ErrorBoundaryProps, ErrorBoundaryState> {
  state: ErrorBoundaryState = { hasError: false };

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    console.error('Unhandled error in the storefront UI:', error, errorInfo);
  }

  render(): ReactNode {
    if (this.state.hasError) {
      return (
        <Container maxWidth="sm" sx={{ py: 8 }}>
          <Stack spacing={2} sx={{ alignItems: 'flex-start' }}>
            <Typography variant="h4" component="h1">
              Something went wrong
            </Typography>
            <Typography color="text.secondary">
              We hit an unexpected error. Reloading the page usually fixes it.
            </Typography>
            <Button variant="contained" onClick={() => window.location.assign('/')}>
              Back to home
            </Button>
          </Stack>
        </Container>
      );
    }

    return this.props.children;
  }
}
