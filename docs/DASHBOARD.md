# Dashboard Development Guide

## Overview

React-based UI for testing DaprMQ operations. Communicates with API server via REST (no gRPC).

**Stack:** React 18.2, TypeScript 5.2, Vite 5.0.8, CSS Modules

**Architecture:**
```
Browser (:3000) → Vite Dev Proxy → API Server (:8002) → QueueActor → State Store
```

## Quick Start

```bash
cd dashboard
npm install
npm run dev      # Starts on http://localhost:3000
```

**Production:**
```bash
npm run build    # Output: dist/
npm run preview  # Test production build
docker build -t daprmq-dashboard .
```

## Project Structure

```
dashboard/src/
├── components/          # UI components (CSS Modules)
│   ├── QueueHeader.tsx  # Queue ID selector + stats
│   ├── PushSection.tsx  # Priority-based push form
│   ├── PopSection.tsx   # Pop/PopWithAck controls
│   ├── MessagesList.tsx # Message list container
│   ├── MessageItem.tsx  # Individual message + lock UI
│   └── ErrorModal.tsx   # Error notification overlay
├── hooks/
│   ├── useQueueOperations.ts  # Core state + API orchestration
│   └── useQueueId.ts          # URL param sync
├── services/
│   └── queueApi.ts      # API client (fetch wrapper)
├── types/
│   └── queue.ts         # TypeScript interfaces
└── utils/
    └── queueHelpers.ts  # ID generation, validation
```

## Key Files

### State Management: [useQueueOperations.ts](../dashboard/src/hooks/useQueueOperations.ts)

Central hook managing all queue operations. Encapsulates API calls, loading states, error handling.

**Pattern:**
```typescript
const {
  messages,
  isLoading,
  error,
  handlePush,
  handlePop,
  handlePopWithAck,
  handleAcknowledge,
  handleDeadLetter
} = useQueueOperations(queueId);
```

### API Client: [queueApi.ts](../dashboard/src/services/queueApi.ts)

Thin wrapper around `fetch`. Handles base URL (`VITE_API_BASE_URL`), error responses, JSON parsing.

**All endpoints return:**
```typescript
{ success: true, data: T } | { success: false, error: string, status?: number }
```

### Type Definitions: [queue.ts](../dashboard/src/types/queue.ts)

TypeScript interfaces for API contracts. Must match backend models.

## Development Patterns

### Component Guidelines

**DO:**
- Use CSS Modules for styling (`.module.css`)
- PascalCase for component files/names
- Keep components small (< 150 lines)
- Colocate styles with components

**DON'T:**
- Add external state management (Redux, Zustand, etc.)
- Use inline styles
- Create utility components prematurely

### State Management

**Local state only.** Use React hooks (`useState`, `useEffect`). No global state library.

**Pattern:** Lift state to `useQueueOperations` for cross-component coordination.

### API Integration

**Base URL:** `VITE_API_BASE_URL` env variable (default: `http://localhost:8002`)

**Dev proxy:** Vite proxies `/queue/*` to API server ([vite.config.ts](../dashboard/vite.config.ts))

**Endpoints:**
- `POST /queue/{id}/push` - Push message
- `POST /queue/{id}/pop` - Pop (immediate removal)
- `POST /queue/{id}/pop` (header: `require-ack: true`) - Pop with lock
- `POST /queue/{id}/acknowledge` - Acknowledge locked message
- `POST /queue/{id}/deadletter` - Route to DLQ

### Error Handling

**HTTP Status → UI Action:**
- `204` - Empty queue, show empty state
- `400` - Validation error, show in modal
- `404` - Queue not found, show in modal
- `410` - Lock expired, show in modal
- `423` - Item locked, show in modal
- `500` - Server error, show in modal

**Pattern:** Check `response.success` before accessing `response.data`

## Features

### Queue Management

**URL persistence:** `?queue_name=my-queue` syncs with browser history ([useQueueId.ts](../dashboard/src/hooks/useQueueId.ts))

**Queue naming:** Alphanumeric + `-_` only. Validated client-side.

### Message Publishing

**Priority levels:**
- `0` - Fast lane (urgent)
- `1+` - Normal priority (default: 1)

**Auto-generated payload:**
```json
{
  "userId": "user_123",
  "action": "login|logout|purchase|signup",
  "timestamp": "2024-03-28T10:30:00Z"
}
```

### Lock Management

**TTL:** 30 seconds (hardcoded in `PopWithAck`)

**UI shows:**
- Lock countdown timer
- Acknowledge button (green)
- Dead letter button (red)

**Lock expired:** Item remains locked but acknowledgement fails with 410

### Dead Letter Queue

**Naming:** `{originalQueueId}-deadletter` (auto-appended)

**Visual indicators:** DLQ queues show warning badge in UI

**Flow:** Locked message → route to DLQ → appears in `{id}-deadletter` queue

## Configuration

### Environment Variables

**Development:** `.env`
```
VITE_API_BASE_URL=http://localhost:8002
```

**Production:** `.env.production`
```
VITE_API_BASE_URL=http://api-server:8002
```

### Vite Config: [vite.config.ts](../dashboard/vite.config.ts)

**Dev proxy:**
```typescript
proxy: {
  '/queue': {
    target: 'http://localhost:8002',
    changeOrigin: true
  }
}
```

**Build:** Outputs to `dist/`, served by Nginx in Docker

### TypeScript Config: [tsconfig.json](../dashboard/tsconfig.json)

- Target: ES2020
- Module: ESNext (tree-shaking)
- JSX: React JSX (automatic)
- Strict mode: Enabled

## Testing Locally

**Start API server first:**
```bash
cd dotnet/src/DaprMQ.ApiServer
dapr run --app-id daprmq-api --app-port 5000 --resources-path ../../dapr/components -- dotnet run
```

**Then start dashboard:**
```bash
cd dashboard
npm run dev
```

**Or use Docker Compose:**
```bash
docker-compose up  # Starts full stack
```

Navigate to `http://localhost:3000`

## Docker Build

**Multi-stage:** Node build → Nginx serve

**Dockerfile:**
```dockerfile
FROM node:18-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:alpine
COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
```

**Nginx config:** SPA routing (fallback to `index.html`), asset caching

## Common Tasks

### Add New API Endpoint

1. Add method to [queueApi.ts](../dashboard/src/services/queueApi.ts)
2. Add handler to [useQueueOperations.ts](../dashboard/src/hooks/useQueueOperations.ts)
3. Wire to UI component

### Add New Component

1. Create `ComponentName.tsx` + `ComponentName.module.css`
2. Import into parent component
3. Keep under 150 lines

### Update API Models

1. Update [queue.ts](../dashboard/src/types/queue.ts) types
2. Update [queueApi.ts](../dashboard/src/services/queueApi.ts) return types
3. Update component props

## Conventions

**Naming:**
- Components: PascalCase (`QueueHeader.tsx`)
- Utilities: camelCase (`queueHelpers.ts`)
- CSS Modules: `ComponentName.module.css`
- Hooks: `use` prefix (`useQueueOperations.ts`)

**Styling:**
- CSS Modules for scoped styles
- CSS Variables for theming (`:root` in `index.css`)
- Mobile-first responsive design
- No Tailwind/CSS-in-JS

**Code Style:**
- Prefer functional components
- Destructure props
- Use TypeScript strict mode
- No `any` types

## Troubleshooting

**CORS errors:** Check API server is running and `VITE_API_BASE_URL` is correct

**404 on API calls:** Verify Vite proxy config matches API server port

**Empty queue messages:** Normal behavior, not an error (204 status)

**Lock expired errors:** Expected after 30s TTL, user should pop new message

**Build failures:** Clear `node_modules` and `dist/`, run `npm ci`
