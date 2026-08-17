FROM nginx:1.28-alpine

RUN apk add --no-cache openssl

COPY docker-entrypoint-tls.sh /usr/local/bin/docker-entrypoint-tls.sh
RUN chmod +x /usr/local/bin/docker-entrypoint-tls.sh

ENTRYPOINT ["docker-entrypoint-tls.sh"]
CMD ["nginx", "-g", "daemon off;"]
