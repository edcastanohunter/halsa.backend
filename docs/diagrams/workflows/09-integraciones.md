# 09 · Integraciones (sistemas de la ARL y de la IPS)

Estos actores **no son personas**. Son service accounts de Keycloak (`int-arl-{codigo}`, `int-ips-{reps}`):

- autenticadas con `private_key_jwt`;
- con tokens **amarrados al certificado del cliente** (mTLS, RFC 8705);
- y con una `Idempotency-Key` obligatoria en cada escritura.

No tienen pantallas: aquí se documentan porque **inician** o **alimentan** los flujos de las personas.

## 1. El core de la ARL radica un caso

```mermaid
sequenceDiagram
    autonumber
    participant CORE as Core ARL
    participant NGX as Nginx (mTLS)
    participant API as API Halsa
    participant W as Worker

    CORE->>NGX: POST /v1/integraciones/furat (o /furep)<br/>certificado cliente + token + Idempotency-Key
    NGX->>API: Reenvía (solo con un certificado válido)
    API->>API: Valida el token, el tenant y la huella del certificado (claim cnf)
    alt Misma llave, otro cuerpo
        API-->>CORE: 409
    else Misma llave, mismo cuerpo
        API-->>CORE: La respuesta original (no duplica)
    else Nueva
        API->>API: Una transacción: caso RADICADO + mensaje crudo cifrado<br/>+ reporte inicial clínico cifrado
        API-->>CORE: 202 Accepted { radicado, solicitudId }
        API-)W: CasoRadicado (outbox)
        W->>W: Abre los plazos y valida la afiliación y los duplicados
        W-->>CORE: webhook caso.validado o caso.rechazado
    end
    CORE->>API: GET /v1/integraciones/furat/{solicitudId} (consultar el estado)
```

## 2. El sistema de la IPS envía información clínica

```mermaid
flowchart LR
    I1["Sistema de la IPS<br/>(int-ips-REPS)"]:::sistema -- "POST /integraciones/conceptos-medicos<br/>(caso, profesional, contenido)" --> I2["Registro clínico cifrado"]:::clinico
    I1 -- "POST /casos/{id}/incapacidades<br/>(multipart + PDF)" --> I3["Incapacidad + soporte cifrado"]:::clinico
    I2 --> I4["Webhook concepto.registrado"]:::sistema
    I3 --> I5["Webhook incapacidad.registrada<br/>+ recálculo de TOPE_IT_180 ⏱"]:::sistema

    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
```

La IPS solo puede escribir en casos **remitidos** a ella.

## 3. Webhooks que salen hacia los sistemas suscritos

```mermaid
flowchart TD
    OUT["Outbox (misma transacción que el cambio)"]:::sistema --> WD["Worker: WebhookDispatcher<br/>firma HMAC-SHA256 (Halsa-Signature)<br/>reintentos con backoff durante 24 h → DEAD_LETTER"]:::sistema
    WD --> E1["caso.validado · caso.rechazado"]:::accion
    WD --> E2["caso.remitido · remision.revocada"]:::accion
    WD --> E3["plazo.proximo-a-vencer · plazo.vencido"]:::plazo
    WD --> E4["incapacidad.registrada · concepto.registrado"]:::accion
    WD --> E5["dictamen.notificado (sin fundamentos)"]:::accion
    WD --> E6["auditoria.acceso-emergencia"]:::riesgo

    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

- **Entrega:** al menos una vez. El receptor deduplica por `eventoId`.
- **Gestión:** las suscripciones (`/v1/integraciones/suscripciones-webhook`) las gestionan los propios sistemas (`webhooks:manage`). Ninguna persona tiene ese permiso, así que no hay pantalla para ellas.
