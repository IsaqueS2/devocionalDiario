# Devocional

Aplicação web ASP.NET Core com registros compartilhados em PostgreSQL. Ela usa o `DATABASE_URL` do Neon CLI, uma connection string Npgsql ou o PostgreSQL local do Docker Compose.

## Configuração Neon

O branch `production` do projeto `noisy-fire-30971095` já foi vinculado e implantado com Neon CLI. Para repetir a configuração em outro ambiente:

```powershell
npm install --global neon@latest
neon login
neon init -y --org-id org-lively-sea-55205931 --project-id noisy-fire-30971095 --branch production --services postgres
neon deploy
```

O deploy cria/atualiza o PostgreSQL definido pela configuração Neon e grava as variáveis localmente. Por padrão, `DATABASE_URL` e `DATABASE_URL_UNPOOLED` ficam em `.env.local` (ou em `.env` se esse arquivo já existir). O app carrega `DATABASE_URL` desses arquivos sem exibir seu valor. Os arquivos `.env*` são ignorados pelo Git.

## Compilar e executar

É necessário ter o SDK do .NET 10 e Node.js/npm instalados. Os scripts npm encaminham os comandos ao app .NET:

```powershell
npm run build
npm start
```

Abra o endereço local que o ASP.NET mostrar no terminal. Em hospedagem, configure `DATABASE_URL` como variável de ambiente secreta, usando a connection string pooled fornecida pelo Neon para tráfego da aplicação. O app aceita o formato URI `postgresql://` do Neon CLI, converte `sslmode` e `channel_binding` para o Npgsql e exige TLS. `NEON_CONNECTION_STRING` continua disponível para strings no formato padrão Npgsql `Host=...;Database=...;Username=...;Password=...`.

O Neon hospeda o banco PostgreSQL, não o servidor ASP.NET. Publique o contêiner definido pelo `Dockerfile` em um host que aceite Docker e configure `DATABASE_URL` no painel desse host. A aplicação escuta na porta `8080`; configure HTTPS no domínio público. O endpoint `/healthz` retorna `{"status":"ok"}` quando o app está conectado ao banco.

## Execução local com Docker Compose

Para usar o PostgreSQL local, copie o exemplo e inicie os serviços:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

O PostgreSQL local usa o volume Docker `postgres_data`. Não remova esse volume para preservar os registros.

Para executar os contêineres usando o Neon provisionado, depois de `neon deploy` rode:

```powershell
docker compose --env-file .env.example --env-file .env.local up -d --build
```

O banco local ainda inicia pelo Compose, mas o app usa `DATABASE_URL`. Não coloque valores reais de conexão em `.env.example` nem envie `.env` ou `.env.local` ao Git.

## Banco e API

Na inicialização, o app cria a tabela `registros` se ela ainda não existir; o usuário PostgreSQL precisa ter permissão para criar e modificar essa tabela.

- `GET /api/devocional/{ano}/{mes}` lista os registros do mês.
- `POST /api/devocional/alternar` recebe `{"data":"AAAA-MM-DD","usuario":"Nome"}` e alterna o registro.
- `GET /healthz` verifica a saúde da aplicação.

O site e os registros são públicos e compartilhados. Não há login: qualquer visitante pode registrar um nome e marcar ou desmarcar devocionais, inclusive registros atribuídos a outros nomes. Não use dados pessoais ou informações que precisem de controle de acesso.
