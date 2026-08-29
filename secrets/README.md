# secrets/

Docker Compose monta cada fichero de esta carpeta en `/run/secrets/<nombre>` dentro de los
contenedores que lo declaran. El valor **nunca** entra en una capa de imagen ni aparece en
`docker inspect`.

## Antes del primer `docker compose up`

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt
```

Si no tienes `openssl`, vale cualquier cadena larga:

```bash
echo "una-cadena-larga-y-aleatoria" > secrets/lab_shared_key.txt
```

`lab_shared_key.txt` está en `.gitignore`: cada persona genera el suyo. En producción este
fichero lo sustituye el gestor de secretos de la plataforma (Kubernetes Secrets, Azure Key
Vault, AWS Secrets Manager), sin tocar el código: la aplicación sigue leyendo una ruta.
