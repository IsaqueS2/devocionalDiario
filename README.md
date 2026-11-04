# Devocional

Aplicação web para acompanhar devocionais diários. A interface e a API compartilham um banco PostgreSQL persistente, executado junto com a aplicação — sem Supabase ou outro serviço de backend gerenciado.

## Executar localmente

Instale o Docker Desktop, copie o arquivo de exemplo e inicie os serviços:

```powershell
Copy-Item .env.example .env
docker compose up -d --build
```

Abra [http://localhost](http://localhost). O PostgreSQL fica em um volume Docker chamado `postgres_data`, que sobrevive a reinicializações e reconstruções dos contêineres. Não remova esse volume para preservar os registros.

## Publicar em um VPS

Você precisa de um VPS Linux com Docker Compose, um domínio e os registros DNS `A`/`AAAA` do domínio apontando para o VPS. Libere as portas TCP 80 e 443 no firewall do servidor. O Caddy obtém e renova automaticamente o certificado HTTPS.

No VPS:

1. Clone este repositório e entre na pasta do projeto.
2. Crie o arquivo de configuração e gere uma senha forte para o banco:

   ```sh
   cp .env.example .env
   openssl rand -hex 32
   ```

3. Edite `.env`: defina `POSTGRES_PASSWORD` com a senha gerada e `DOMAIN` com o domínio público, sem `https://`.
4. Inicie o site:

   ```sh
   docker compose up -d --build
   ```

Depois que o DNS propagar, acesse `https://seu-dominio`. Para atualizar após enviar mudanças ao Git:

```sh
git pull
docker compose up -d --build
```

O arquivo `.env` contém credenciais e não deve ser enviado ao Git. O banco não expõe sua porta à internet e seus dados ficam no volume `postgres_data`. O volume protege contra recriações dos contêineres, mas não contra perda ou falha do VPS: mantenha cópias de segurança fora do servidor.

Para criar uma cópia de segurança manual no VPS:

```sh
docker compose exec -T db sh -c 'pg_dump -U "$POSTGRES_USER" "$POSTGRES_DB"' > backup.sql
```

## Acesso e dados

O site e os registros são públicos e compartilhados. Como solicitado, não há login: qualquer visitante pode registrar um nome e marcar ou desmarcar devocionais, inclusive registros atribuídos a outros nomes. Não use dados pessoais ou informações que precisem de controle de acesso.

A aplicação cria a tabela automaticamente na primeira inicialização. O banco SQLite usado anteriormente neste computador estava vazio quando a configuração PostgreSQL foi preparada; novos registros serão gravados no PostgreSQL do servidor.
