# Gözleme standard development commands. Run `make help`.
SHELL := /usr/bin/env bash
PYTHON ?= $(if $(wildcard .venv/bin/python),.venv/bin/python,python3)

.PHONY: help bootstrap build test test-backend test-frontend lint up down logs reset ps validate demo helm-lint

help: ## List commands
	@grep -E '^[a-z-]+:.*##' $(MAKEFILE_LIST) | awk 'BEGIN{FS=":.*## "}{printf "  %-14s %s\n",$$1,$$2}'

bootstrap: ## Install local toolchain deps (pnpm packages, Python governance deps)
	corepack enable
	pnpm install --frozen-lockfile
	python3 -m venv .venv && .venv/bin/pip install -q -r requirements.txt

build: ## Build backend and frontend
	dotnet build Gozleme.slnx -c Release
	pnpm build

test: test-backend test-frontend ## Run all unit and integration tests

test-backend: ## Backend unit + Testcontainers integration tests (needs Docker)
	dotnet test --solution Gozleme.slnx

test-frontend: ## Frontend unit tests
	pnpm test

lint: ## Format/lint/type checks
	dotnet format Gozleme.slnx --verify-no-changes
	pnpm lint
	pnpm typecheck

up: ## Start the full stack (web :3000, api :8080) and wait until healthy
	docker compose up -d --build --wait

down: ## Stop the stack (keeps database volume)
	docker compose down

logs: ## Follow stack logs
	docker compose logs -f

ps: ## Show stack status
	docker compose ps -a

reset: ## Stop the stack and delete ALL volumes (database, Loki logs, Prometheus metrics)
	docker compose down -v --remove-orphans

validate: ## Governance: registry/manifest schemas, prompts, register sync, branch-name rules
	$(PYTHON) scripts/validate_docs.py
	$(PYTHON) scripts/generate_register.py --check
	scripts/check_branch_name.sh --self-test

helm-lint: ## Lint and render the Helm chart (uses Docker if helm is not installed)
	scripts/helm.sh lint deploy/helm/gozleme
	scripts/helm.sh template gozleme deploy/helm/gozleme > /dev/null

demo: ## Cumulative autonomous Playwright demo for SPEC (default: latest spec with a demo); writes that spec's evidence/
	scripts/demo/run-demo.sh $(SPEC)
