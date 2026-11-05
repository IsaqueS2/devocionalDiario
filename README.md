# Devocional

Aplicação web para acompanhar devocionais diários. A API grava os registros em PostgreSQL. Ela pode usar um banco Neon pela variável `NEON_CONNECTION_STRING` ou um PostgreSQL local pelo Docker Compose.

## Usar Neon

O Neon hospeda o banco PostgreSQL, mas não executa esta aplicação ASP.NET. Publique o contêiner da aplicação em uma hospedagem que aceite Docker e configure a variável de ambiente `NEON_CONNECTION_STRING` nessa hospedagem:

1. No painel do Neon, abra o projeto e selecione o formato de conexão para **.NET/Npgsql**. Prefira a conexão pooled indicada pelo Neon para aplicações web. A connection string do Npgsql usa o formato `Host=...;Database=...;Username=...;Password=...;SSL Mode=Require;` — não use a URI `postgresql://...` de outros drivers.
2. Confira que a string exige TLS (`SSL Mode=Require`). Trate-a como senha: não a coloque no Git, no código, em capturas de tela ou em mensagens públicas.
3. Na hospedagem da aplicação, crie a variável secreta `NEON_CONNECTION_STRING` com o valor copiado. A imagem é construída pelo `Dockerfile` da raiz; a aplicação escuta na porta `8080`.
4. Publique a aplicação e abra `/healthz` no domínio gerado. Uma resposta `{"status":"ok"}` confirma que o servidor está no ar e conectado ao banco.

No primeiro início, a aplicação cria a tabela `registros` se ela ainda não existir. O usuário do banco precisa ter permissão para criar essa tabela e inserir, consultar e remover registros. A variável `NEON_CONNECTION_STRING` tem precedência sobre a conexão local.

Configure também HTTPS no domínio público. Não coloque credenciais no código nem use `appsettings.json` versionado para guardar a connection string. Se uma senha do Neon for exposta, gere outra no painel do Neon.

## Executar localmente

É necessário ter Docker Desktop. Copie o arquivo de exemplo e inicie os serviços:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

Abra [http://localhost](http://localhost). Por padrão, o Compose usa o PostgreSQL local em um volume Docker chamado `postgres_data`, que sobrevive a reinicializações e reconstruções dos contêineres. Não remova esse volume para preservar os registros locais.

Para usar o banco Neon com o Compose local, preencha `NEON_CONNECTION_STRING` no `.env` com a connection string do painel. A conexão Neon será usada no lugar do banco local. O contêiner local do PostgreSQL ainda será iniciado pelo Compose, mas não será usado pela aplicação.

## Publicar um VPS com PostgreSQL local

Você precisa de um VPS Linux com Docker Compose, um domínio e os registros DNS `A`/`AAAA` do domínio apontando para o VPS. Libere as portas TCP 80 e 443 no firewall do servidor. O Caddy obtém e renova automaticamente o certificado HTTPS.

No VPS:

1. Clone este repositório e entre na pasta do projeto.
2. Crie o arquivo de configuração e gere uma senha forte para o banco:

   ```sh
   cp .env.example .env
   openssl rand -hex 32
   ```

3. Edite `.env`: defina `POSTGRES_PASSWORD` com a senha gerada, deixe `NEON_CONNECTION_STRING` vazio e defina `DOMAIN` com o domínio público, sem `https://`.
4. Inicie o site:

   ```sh
   docker compose up -d --build
   ```

O arquivo `.env` contém credenciais e não deve ser enviado ao Git. O PostgreSQL local não expõe sua porta à internet e seus dados ficam no volume `postgres_data`. O volume protege contra recriações dos contêineres, mas não contra perda ou falha do VPS: mantenha cópias de segurança fora do servidor.

## API

- `GET /api/devocional/{ano}/{mes}` lista os registros do mês.
- `POST /api/devocional/alternar` recebe `{"data":"AAAA-MM-DD","usuario":"Nome"}` e alterna o registro.
- `GET /healthz` verifica a saúde da aplicação.

O site e os registros são públicos e compartilhados. Não há login: qualquer visitante pode registrar um nome e marcar ou desmarcar devocionais, inclusive registros atribuídos a outros nomes. Não use dados pessoais ou informações que precisem de controle de acesso.
