# PROMPT PARA CLAUDE: ARQUITECTURA BACKEND, DATOS E INTEGRACIÓN - PLATAFORMA MÉDICA ARL COLOMBIA

**Rol:** Actúa como Arquitecto de Soluciones Cloud, Especialista en Backend/Integración HealthTech y Experto en Legislación de Seguridad y Salud en el Trabajo (SST) en Colombia.

**Contexto del Proyecto:**
Necesito diseñar la arquitectura de software backend, la estrategia de integración externa y el modelo de datos para una plataforma SaaS de grado de producción enfocada en la gestión integral de siniestros laborales (ATEL) para las ARLs en Colombia. Debe integrarse en tiempo real con sistemas core de las ARLs y redes de IPS.

**Stack Tecnológico Definido:**

- **Backend Core:** ASP.NET Core (C#) con Entity Framework Core.
- **Base de Datos:** PostgreSQL.
- **Gestión de Identidad (IdP):** Keycloak (OAuth2, OpenID Connect).
- **Despliegue y Tareas:** Docker, Nginx (reverse proxy), Jobs en background (Quartz.NET o Hangfire).

---

## 1. MARCO LEGAL Y PRIVACIDAD (Secreto Médico)

- **Reserva de Historia Clínica (Res. 1995/1999 y Ley 1581/2012):** Estrategia en PostgreSQL y mappers de EF Core para que el detalle clínico esté completamente aislado y encriptado.
- **Auditoría Legal:** Diseño de un log de auditoría inmutable que registre cada lectura/escritura de un expediente médico, válido ante la Supersalud.
- **Términos de Ley (SLA):** Sistema de jobs en background para calcular y disparar webhooks o eventos ante vencimientos según el Decreto 1072/2015.

## 2. INTEGRACIÓN MULTI-ACTOR Y MULTI-TENANCY

- **Integración ARLs e IPS:** API RESTful/Webhooks para recepción de FURAT/FUREP. Endpoints para carga de conceptos médicos (considerando futura interoperabilidad HL7 FHIR).
- **BFF (Backend for Frontend):** Endpoints optimizados para el Portal Paciente.
- **Keycloak y Multi-Tenancy:** Configuración de Realms y Clients. Estrategia en DB/EF Core (¿Discriminador, esquema o DB por ARL?) garantizando el Habeas Data.

---

## 3. ENTREGABLES ESPERADOS

Entrega una especificación técnica de arquitectura backend que incluya:

1. **Diagrama de Arquitectura Lógica:** En texto o Mermaid, desde Nginx, pasando por Keycloak, API en .NET, hasta PostgreSQL.
2. **Modelo de Datos (EF Core):** Código en C# modelando `CasoMedico` vs `HistoriaClinicaDetalle` asegurando que consultas administrativas no expongan data clínica.
3. **Contrato de API (api.spec / Swagger conceptual):** Define estrictamente la estructura del payload JSON para el webhook del FURAT y el endpoint seguro de carga de incapacidad. Este contrato será consumido por el equipo de Frontend.
4. **Validación de Autorización Backend:** Cómo el middleware en .NET valida los tokens JWT de Keycloak y los claims para accesos sensibles (ej. dictamen PCL).
