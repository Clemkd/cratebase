# Cratebase — image unique : API, console d'administration, base et fichiers.
#
# Rien à côté au démarrage : pas de PostgreSQL, pas de MinIO, pas de nginx. C'est l'engagement du
# §8 du document de conception — et la même image accepte PostgreSQL et S3 par configuration, sans
# être reconstruite.

# ── Console d'administration ───────────────────────────────────────────────────────────────────
FROM node:24-alpine AS spa

WORKDIR /spa

COPY web/admin/package.json web/admin/package-lock.json* ./

# `npm install` et non `npm ci`, volontairement : Tailwind v4, Rolldown et lightningcss embarquent
# des binaires natifs optionnels, et un lockfile produit sous Windows fait échouer `npm ci` sous
# Linux. Piège déjà rencontré et documenté dans l'inventaire du parc.
RUN npm install --no-audit --no-fund

COPY web/admin/ ./
COPY web/admin/tsconfig.json ./tsconfig.json

# La sortie de Vite pointe vers le wwwroot de l'application ; ici on la redirige vers /spa/dist.
RUN npx vite build --outDir /spa/dist --emptyOutDir

# ── Compilation .NET ───────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build

WORKDIR /src

# Les fichiers de projet d'abord : la couche de restauration est réutilisée tant que les
# dépendances ne bougent pas.
COPY Directory.Build.props Directory.Packages.props ./

# ⚠️ Cette liste doit couvrir TOUS les projets de la chaîne de dépendances. Un projet oublié n'est
# pas restauré, et l'erreur ne survient qu'à la publication, plusieurs étapes plus loin, sous la
# forme obscure « Assets file project.assets.json not found ».
COPY src/Cratebase.Core/*.csproj              src/Cratebase.Core/
COPY src/Cratebase.Expressions/*.csproj       src/Cratebase.Expressions/
COPY src/Cratebase.Data/*.csproj              src/Cratebase.Data/
COPY src/Cratebase.Data.Sqlite/*.csproj       src/Cratebase.Data.Sqlite/
COPY src/Cratebase.Data.Postgres/*.csproj     src/Cratebase.Data.Postgres/
COPY src/Cratebase.Schema/*.csproj            src/Cratebase.Schema/
COPY src/Cratebase.Records/*.csproj           src/Cratebase.Records/
COPY src/Cratebase.Auth/*.csproj              src/Cratebase.Auth/
COPY src/Cratebase.Storage/*.csproj           src/Cratebase.Storage/
COPY src/Cratebase.Storage.S3/*.csproj        src/Cratebase.Storage.S3/
COPY src/Cratebase.Server/*.csproj            src/Cratebase.Server/
COPY src/Cratebase.App/*.csproj               src/Cratebase.App/

RUN dotnet restore src/Cratebase.App/Cratebase.App.csproj

COPY src/ src/

# La console compilée entre avant la publication : MapStaticAssets calcule alors les empreintes de
# contenu et les variantes compressées au moment de la publication, pas à l'exécution.
COPY --from=spa /spa/dist/ src/Cratebase.App/wwwroot/

RUN dotnet publish src/Cratebase.App/Cratebase.App.csproj \
      --no-restore -c Release -o /app

# ── Exécution ──────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

WORKDIR /app

# curl sert au HEALTHCHECK. L'image de base n'embarque ni curl ni wget, et son shell est « dash » :
# la redirection /dev/tcp, réflexe habituel pour éviter une dépendance, n'existe pas là-bas.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*

# Utilisateur non privilégié, propriétaire du volume de données.
RUN useradd --system --uid 64198 --create-home cratebase \
 && mkdir -p /data \
 && chown -R cratebase:cratebase /data

COPY --from=build --chown=cratebase:cratebase /app ./

USER cratebase

# ⚠️ 0.0.0.0 explicitement, et non ASPNETCORE_HTTP_PORTS.
#
# Avec le seul numéro de port, Kestrel se lie à « [::] » — et dans ce conteneur, la pile n'est pas
# en double adressage : rien n'écoute en IPv4. Docker redirige le port publié vers l'adresse IPv4
# du conteneur, donc toute connexion depuis l'hôte reste en attente jusqu'au délai d'expiration.
# Le conteneur paraît sain, les journaux annoncent « Now listening on http://[::]:8090 », et
# personne ne peut s'y connecter.
ENV ASPNETCORE_URLS=http://0.0.0.0:8090 \
    ASPNETCORE_ENVIRONMENT=Production \
    Cratebase__DataDirectory=/data

# Base SQLite, fichiers importés et sauvegardes. À monter en volume : sans cela, tout disparaît au
# remplacement du conteneur.
VOLUME ["/data"]

EXPOSE 8090

HEALTHCHECK --interval=30s --timeout=3s --start-period=15s --retries=3 \
  CMD curl --fail --silent http://127.0.0.1:8090/api/health | grep -q '"status":"ok"'

ENTRYPOINT ["dotnet", "Cratebase.App.dll"]
