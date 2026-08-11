FROM nginx:1.28-alpine

# openssl CLI isn't in the base image; docker-entrypoint-tls.sh needs it to
# synthesize a throwaway TLS cert when compose/nginx/certs/ is empty (fresh
# clone, CI runner) so nginx has something to boot with.
RUN apk add --no-cache openssl

COPY docker-entrypoint-tls.sh /usr/local/bin/docker-entrypoint-tls.sh
RUN chmod +x /usr/local/bin/docker-entrypoint-tls.sh

ENTRYPOINT ["docker-entrypoint-tls.sh"]
CMD ["nginx", "-g", "daemon off;"]
