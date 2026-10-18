# 07 · Trabajador o paciente (portal del paciente)

**Quién es:** el trabajador accidentado o enfermo.

**Cómo entra:**

- Tiene su propia cuenta en el realm `halsa-pacientes` y la crea él mismo.
- Entra **a través del BFF**: su navegador nunca tiene un token, solo una cookie de sesión.

**Qué ve:** estados e **hitos administrativos** de sus casos, sus incapacidades y sus solicitudes. **Nunca** diagnósticos, porcentajes ni fundamentos.

**Usuario de desarrollo:** `paciente.dev` / `dev`.

## Mapa de navegación

```mermaid
flowchart LR
    BIE["Bienvenida<br/>(sin sesión)"]:::pantalla --> KC["Login o registro<br/>(páginas de Keycloak)"]:::externo
    KC --> VIN{"¿Cuenta vinculada?"}
    VIN -- no --> ACT["Activar<br/>(canjear código)"]:::pantalla
    ACT --> INI
    VIN -- sí --> INI["Inicio<br/>casos activos, incapacidad vigente,<br/>próximos hitos"]:::pantalla
    INI --> CAS["Mis casos"]:::pantalla --> CAS1["Caso: línea de tiempo"]:::pantalla --> SOLN["Solicitar copia de mi HC"]:::pantalla
    INI --> INC["Incapacidades"]:::pantalla
    INI --> SOL["Mis solicitudes"]:::pantalla
    INI --> CTA["Cuenta<br/>vincular otro código, cerrar sesión"]:::pantalla
    CTA --> ACT

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

## Flujo: de "recibí un código" a "veo mi caso"

```mermaid
sequenceDiagram
    autonumber
    actor AN as Analista ARL
    actor T as Trabajador
    participant P as Portal (navegador)
    participant BFF as BFF paciente
    participant KC as Keycloak (halsa-pacientes)
    participant API as API Halsa

    AN-->>T: Entrega el código ABC123-K7Q2M9XD4P (vence en 30 días)
    T->>P: Entra al portal → "Ingresar" o "Crear cuenta"
    P->>BFF: /paciente/bff/login (navegación completa)
    BFF->>KC: Código + PKCE (cliente confidencial)
    T->>KC: Se registra o inicia sesión
    KC-->>BFF: Tokens (se quedan en el servidor)
    BFF-->>P: Cookie de sesión HttpOnly. Ningún token al navegador
    P->>BFF: GET /paciente/bff/usuario
    BFF-->>P: { vinculado: false } → pantalla Activar
    T->>P: Escribe el código
    P->>BFF: POST /paciente/api/vinculos (+ X-Halsa-Csrf)
    BFF->>API: POST /v1/paciente/vinculos (Bearer del paciente)
    alt Código inválido, usado o vencido
        API-->>P: Paciente.CodigoInvalido
    else 5 intentos fallidos con ese código
        API-->>P: El código queda BLOQUEADO
    else Demasiados intentos desde la misma IP
        BFF-->>P: 429 (5 por minuto): esperar
    else Válido
        API-->>P: 201 Vinculado al documento CC ******4050
        Note over API: El vínculo es POR DOCUMENTO.<br/>Ve los casos de cualquier ARL con ese documento
    end
    P->>BFF: GET /paciente/api/resumen
    BFF-->>P: Casos activos, incapacidades vigentes, próximos hitos
```

## Estados del código de activación

```mermaid
stateDiagram-v2
    [*] --> ACTIVO: la ARL lo emite
    ACTIVO --> USADO: el trabajador lo canjea
    ACTIVO --> BLOQUEADO: 5 secretos equivocados
    ACTIVO --> EXPIRADO: pasan 30 días, o la ARL emite uno nuevo
    USADO --> [*]
    BLOQUEADO --> [*]
    EXPIRADO --> [*]
```

## Flujo: solicitar la copia de la historia clínica

```mermaid
flowchart TD
    S1["Caso → 'Solicitar copia de mi historia clínica'"]:::pantalla --> S2["Elige el medio: correo certificado (dirección),<br/>electrónico (correo) o presencial"]:::accion
    S2 --> S3{"¿Ya tiene una solicitud<br/>abierta para ese caso?"}
    S3 -- sí --> S4["409: 'Ya tienes una solicitud abierta'"]:::riesgo
    S3 -- no --> S5["Solicitud RECIBIDA.<br/>La ARL tiene 10 días hábiles ⏱"]:::plazo
    S5 --> S6["Mis solicitudes: Recibida → En trámite →<br/>Entregada o Rechazada (con la nota de la ARL)"]:::pantalla
    S6 --> S7["Recibe la copia por el medio elegido.<br/>Nunca se muestra en línea"]:::externo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

## Lo que aparece en su línea de tiempo

```mermaid
flowchart LR
    H1["Caso radicado"]:::pantalla --> H2["Caso validado<br/>(o rechazado)"]:::pantalla --> H3["Remitido a una IPS"]:::pantalla --> H4["Incapacidad registrada"]:::pantalla --> H5["Te notificaron el dictamen"]:::pantalla --> H6["El dictamen quedó en firme"]:::pantalla

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
```

Los "próximos hitos" del inicio son dos: **fin de la incapacidad** y **último día para controvertir el dictamen**.
