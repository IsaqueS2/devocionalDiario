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

- `GET /api/devocional/{ano}/{mes}` lista os registros do mês com o nome e tema de cada pessoa.
- `POST /api/devocional/alternar` recebe `{"data":"AAAA-MM-DD","usuario":"Nome","tema":"Tema estudado"}` e alterna o registro.
- `PUT /api/devocional` cria ou atualiza o registro da pessoa e recebe `{"data":"AAAA-MM-DD","usuario":"Nome","tema":"Tema estudado"}`; o tema é opcional e tem limite de 200 caracteres.
- `DELETE /api/devocional` remove o registro da pessoa e recebe `{"data":"AAAA-MM-DD","usuario":"Nome"}`.
- `GET /healthz` verifica a saúde da aplicação.

A coluna de tema é adicionada automaticamente na inicialização; os registros existentes continuam válidos e começam sem tema. O retorno mensal tem o formato `{"registros":{"AAAA-MM-DD":[{"usuario":"Nome","tema":"Tema estudado"}]}}`.

O site e os registros são públicos e compartilhados. Não há login: qualquer visitante pode registrar um nome e criar, editar ou remover registros atribuídos a qualquer nome. Cada tema fica associado ao registro daquela pessoa no dia. Não use dados pessoais ou informações que precisem de controle de acesso.

## Lembretes diários às 22h30

O site permite ativar notificações push por aparelho. O lembrete usa o fuso horário do aparelho e é enviado apenas se o nome salvo nele ainda não tiver um registro naquele dia. O agendador do GitHub Actions verifica os horários a cada cinco minutos; como o GitHub pode atrasar execuções programadas, 22h30 é um horário aproximado. A entrega também depende do navegador, do sistema operacional e da conexão do aparelho.

### Configurar os segredos

1. Gere as chaves VAPID e um token aleatório localmente. O comando imprime valores secretos; não compartilhe a saída nem a envie ao Git:

   ```powershell
   dotnet run --project devocional.csproj -- --generate-reminder-secrets
   ```

2. No serviço Web do Render, adicione estas variáveis em **Environment** e faça um deploy:
   - `VAPID_PUBLIC_KEY`: chave pública gerada.
   - `VAPID_PRIVATE_KEY`: chave privada gerada; mantenha-a secreta.
   - `REMINDER_CRON_TOKEN`: token aleatório gerado; mantenha-o secreto.
   - `VAPID_SUBJECT`: `https://devocionaldiario.onrender.com/`.

3. No GitHub, em **Settings → Secrets and variables → Actions**, crie o segredo `REMINDER_CRON_TOKEN` com o mesmo token usado no Render e a variável `RENDER_APP_URL` com `https://devocionaldiario.onrender.com`.

4. No celular, abra o site em HTTPS, informe o nome usado no calendário e toque em **Ativar lembrete**. Aceite as notificações. No iPhone/iPad, use **Compartilhar → Adicionar à Tela de Início**, abra o app instalado e ative o lembrete por lá. O aparelho precisa permitir notificações; o modo de economia de bateria ou as definições do sistema podem atrasá-las.

O nome e fuso horário de cada aparelho, junto com a inscrição push, ficam no PostgreSQL. Os dados existentes são preservados; a tabela de inscrições é criada automaticamente. Para remover o lembrete, use **Desativar** no mesmo aparelho. A chamada manual **Actions → Enviar lembretes do devocional → Run workflow** pode validar o agendador depois da configuração; em outro horário, ela não envia notificações.

O site não tem login: nomes não comprovam identidade e qualquer visitante pode registrar-se em nome de outra pessoa. Assim, um registro com o mesmo nome pode suprimir o lembrete de outro aparelho. A inscrição push é um identificador privado do aparelho e não deve ser compartilhada. Para isolamento real por pessoa é necessário adicionar autenticação.

O Render gratuito pode hibernar quando fica sem tráfego; o agendador envia verificações periódicas para manter o lembrete funcionando, mas isso pode consumir as horas gratuitas do serviço. Um atraso maior que 15 minutos entre execuções pode causar uma hibernação e atrasar o envio até o serviço despertar.
