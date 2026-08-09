import { Link as RouterLink } from 'react-router-dom';
import Stack from '@mui/material/Stack';
import Typography from '@mui/material/Typography';
import Button from '@mui/material/Button';

export function NotFoundPage() {
  return (
    <Stack spacing={2} sx={{ alignItems: 'flex-start', py: 8 }}>
      <Typography variant="h4" component="h1">
        Page not found
      </Typography>
      <Typography color="text.secondary">
        The page you're looking for doesn't exist or may have moved.
      </Typography>
      <Button component={RouterLink} to="/" variant="contained">
        Browse products
      </Button>
    </Stack>
  );
}
