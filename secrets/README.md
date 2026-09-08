# secrets/

Docker Compose monta cada fichero de esta carpeta en `/run/secrets/<nombre>` dentro de los
contenedores que lo declaran. El valor **nunca** entra en una capa de imagen ni aparece en
`docker inspect`.

Hacen falta dos: `lab_shared_key.txt` (la clave que el frontend envía a la API) y
`rabbitmq_password.txt` (la del broker, que leen los tres servicios **y** el propio RabbitMQ).

## Antes del primer `docker compose up`

En Git Bash, WSL, macOS o Linux:

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt
```

```bash
openssl rand -base64 24 > secrets/rabbitmq_password.txt
```

## En PowerShell

`openssl` no está en el PATH (Git for Windows lo trae en `Program Files\Git\usr\bin\`), pero el
problema serio es otro: **la redirección `>` de PowerShell 5.1 escribe UTF-16 con BOM**. El
contenedor de RabbitMQ lee este fichero con `cat` para construir su configuración, y con BOM y
bytes nulos no arranca — el síntoma es un `dependency failed to start` que no apunta a la causa.

Por eso hay que forzar `-Encoding ascii`:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 32; $rng.GetBytes($b); [Convert]::ToBase64String($b) | Out-File -Encoding ascii -NoNewline secrets\lab_shared_key.txt
```

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 24; $rng.GetBytes($b); [Convert]::ToBase64String($b) | Out-File -Encoding ascii -NoNewline secrets\rabbitmq_password.txt
```

Comprobación rápida: bien generados ocupan **44 y 32 bytes**. Si ves 90 y 66, es UTF-16 — bórralos
y repite.

```powershell
Get-ChildItem secrets\*.txt -Exclude *example* | ForEach-Object { "$($_.Name) $($_.Length) bytes" }
```

## En producción

`lab_shared_key.txt` y `rabbitmq_password.txt` están en `.gitignore`: cada persona genera los
suyos. En un despliegue real los sustituye el gestor de secretos de la plataforma (Kubernetes
Secrets, Azure Key Vault con el CSI driver, AWS Secrets Manager) **sin tocar el código**: la
aplicación sigue leyendo una ruta de fichero, que es lo único que cambia.
