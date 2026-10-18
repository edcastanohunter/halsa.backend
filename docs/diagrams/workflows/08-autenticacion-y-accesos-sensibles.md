# 08 · Autenticación y accesos sensibles (todos los usuarios)

## 1. Entrada del personal (ARL e IPS) al backoffice

```mermaid
sequenceDiagram
    autonumber
    actor U as Personal ARL/IPS
    participant SPA as Backoffice (Angular)
    participant KC as Keycloak (realm halsa)
    participant API as API Halsa

    U->>SPA: Abre el backoffice
    SPA->>KC: Código + PKCE (cliente público halsa-backoffice)
    U->>KC: Usuario y contraseña (nivel 1)
    KC-->>SPA: Access token de 5 min (acr=1) + refresh token
    SPA->>API: GET /v1/identidad/yo
    API-->>SPA: { sub, actorTipo, tenantId o ipsId, permisos[] }
    SPA-->>U: Menú, pestañas y botones según los permisos
    loop Cada ~4.5 min
        SPA->>KC: Renueva con el refresh token
    end
```

## 2. Verificación reforzada (step-up a OTP)

```mermaid
sequenceDiagram
    autonumber
    actor U as Médico
    participant SPA as Backoffice
    participant KC as Keycloak
    participant API as API Halsa

    U->>SPA: Abre la historia clínica o el dictamen
    alt El token ya tiene acr=2 (y menos de 15 min, si es el dictamen)
        SPA->>API: GET clínico (Bearer acr=2)
    else Falta la verificación
        SPA-->>U: "Información clínica reservada: verifica tu identidad"
        U->>SPA: "Verificar identidad"
        SPA->>KC: Ventana emergente: acr_values=2 (+ max_age=900 si es el dictamen)
        U->>KC: Código OTP de 6 dígitos
        KC-->>SPA: Token nuevo con acr=2
        SPA->>API: GET clínico (Bearer acr=2)
    end
    alt La API igual pide verificación
        API-->>SPA: 401 WWW-Authenticate: insufficient_user_authentication
        SPA->>KC: Repite la verificación y reintenta una vez
    else Autorizado
        API-->>SPA: 200 (y queda una fila LEER en la auditoría)
    end
```

| Acción | Nivel exigido |
| --- | --- |
| Leer o escribir la historia clínica, descargar el soporte de una incapacidad | `acr=2` |
| Cargar una incapacidad | `acr=2` |
| Leer, registrar o notificar el dictamen PCL | `acr=2` **y** la verificación tiene menos de 15 minutos |

## 3. ¿Puedo ver este contenido clínico? (el árbol de decisión de la API)

```mermaid
flowchart TD
    Q0["Petición clínica"]:::accion --> Q1{"¿Tiene el permiso?<br/>(clinico:read, pcl:dictamen:*)"}
    Q1 -- no --> N1["403, auditado como DENEGADO"]:::riesgo
    Q1 -- sí --> Q2{"¿acr=2?<br/>(¿y menos de 15 min, si es el dictamen?)"}
    Q2 -- no --> N2["401 step-up: pedir OTP"]:::plazo
    Q2 -- sí --> Q3{"¿El caso es visible?<br/>ARL: su tenant · IPS: remisión vigente"}
    Q3 -- no --> N3["404: no existe o no es visible<br/>(no se distingue a propósito)"]:::riesgo
    Q3 -- sí --> Q4{"¿Está asignado al caso?"}
    Q4 -- sí --> OK["200 + auditoría LEER o CREAR"]:::clinico
    Q4 -- no --> Q5{"¿Es una lectura, tiene clinico:emergencia<br/>y envió un motivo de 20–500 caracteres?"}
    Q5 -- no --> N4["403 Clinico.NoAsignado,<br/>auditado como DENEGADO"]:::riesgo
    Q5 -- sí --> EM["200 de emergencia: auditoría con el motivo +<br/>webhook al oficial + revisión de 72 h"]:::clinico

    classDef accion fill:#26a69a0a,stroke:#26a69a,stroke-width:2px
    classDef clinico fill:#7e57c233,stroke:#9575cd,stroke-width:1.5px
    classDef plazo fill:#ffb30033,stroke:#ffb300,stroke-width:1.5px
    classDef riesgo fill:#ef535033,stroke:#ef5350,stroke-width:1.5px
```

Además, la base de datos repite estos controles **por su cuenta**:

- seguridad por filas (RLS) por tenant y por remisión;
- un rol sin acceso al esquema clínico para lo administrativo;
- cifrado de campo.

Si el frontend o la API fallan, la base sigue cerrada.

## 4. Entrada del paciente (BFF, sin tokens en el navegador)

```mermaid
flowchart LR
    P1["Portal en el navegador"]:::pantalla -- "cookie __Host- HttpOnly<br/>+ X-Halsa-Csrf: 1" --> P2["BFF paciente"]:::sistema
    P2 -- "Bearer del paciente<br/>(tokens en Redis, renovados en el servidor)" --> P3["API /v1/paciente/*"]:::sistema
    P2 <-- "código + PKCE<br/>(cliente confidencial)" --> P4["Keycloak<br/>halsa-pacientes"]:::externo

    classDef pantalla fill:#26a69a2e,stroke:#26a69a,stroke-width:1.5px
    classDef sistema fill:#78909c2e,stroke:#90a4ae,stroke-width:1.5px,stroke-dasharray:4 3
    classDef externo fill:#9e9e9e1a,stroke:#9e9e9e,stroke-width:1.5px,stroke-dasharray:2 2
```

- Las rutas del paciente **nunca reciben un id de trabajador**: todo se resuelve desde su cuenta y su vínculo, así que no puede pedir los datos de otro.
- Un token del personal no sirve en las rutas del paciente, ni al revés, porque los emisores son distintos.
